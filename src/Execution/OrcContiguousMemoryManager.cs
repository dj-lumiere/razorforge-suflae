using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;

namespace Builder.Execution;

/// <summary>
/// A JIT object-linking-layer memory manager that allocates every section of one linked object from a
/// SINGLE contiguous virtual-memory slab (bump allocation, in RuntimeDyld's allocation order). This fixes
/// the intermittent Windows-x64 JIT crash <c>IMAGE_REL_AMD64_ADDR32NB relocation requires an ordered
/// section layout</c>: that image-relative relocation (emitted by SEH unwind data) is only resolvable by
/// ORC's RuntimeDyld COFF linker when the referenced section sits at a predictable, ordered address — which
/// the default SectionMemoryManager does not guarantee (it mmaps sections independently, sometimes >2GB
/// apart / out of order). JITLink (LLVM's newer linker that handles COFF SEH natively) is not reachable
/// through the LLVM-C API, so a contiguous slab is the robust in-C-API fix.
///
/// Wired into an LLJIT via <see cref="LLVM.OrcLLJITBuilderSetObjectLinkingLayerCreator"/> + a layer built
/// with <see cref="LLVM.OrcCreateRTDyldObjectLinkingLayerWithMCJITMemoryManagerLikeCallbacks"/>. The
/// per-object context (slab + section ranges) is a managed object kept alive through a <see cref="GCHandle"/>
/// passed as the callbacks' <c>Opaque</c>.
/// </summary>
#pragma warning disable S6640 // unsafe blocks are required for Win32 VirtualAlloc/VirtualProtect and ORC callback interop
internal static unsafe partial class OrcContiguousMemoryManager
{
    // 64 MiB reserved+committed per linked object. Windows demand-pages committed memory, so the resident
    // footprint only grows with the code/data actually written — the reservation is effectively free.
    private const nuint SlabSize = 64 * 1024 * 1024;

    // ── Win32 virtual memory ────────────────────────────────────────────────────────────────────
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_DECOMMIT = 0x4000,
        MEM_RELEASE = 0x8000;

    // Every slab is carved from ONE reserved region, so all objects the JIT links lie within ±2 GB of each
    // other. An AOT-compiled object (the resident base, a resident layer) reaches data in another object
    // through 32-bit PC-relative relocations; calls get stubs, data references do not, so two slabs placed
    // >2 GB apart would silently wrap such a reference. 30 slabs; any two region addresses are < 2^31 apart.
    private const nuint RegionSize = 30 * SlabSize;
    private static byte* _region;
    private static nuint _regionUsed;
    private static readonly Lock RegionLock = new();
    private const uint PAGE_READWRITE = 0x04, PAGE_EXECUTE_READWRITE = 0x40;

    [LibraryImport(libraryName: "kernel32", SetLastError = true)]
    private static partial void* VirtualAlloc(void* addr, nuint size, uint type,
        uint protect);
    [LibraryImport(libraryName: "kernel32", SetLastError = true)]
    private static partial int VirtualFree(void* addr, nuint size, uint type);
    [LibraryImport(libraryName: "kernel32", SetLastError = true)]
    private static partial int VirtualProtect(void* addr, nuint size, uint newProtect,
        uint* oldProtect);
    [LibraryImport(libraryName: "kernel32")]
    private static partial int FlushInstructionCache(nint process, void* addr, nuint size);
    [LibraryImport(libraryName: "kernel32")]
    private static partial nint GetCurrentProcess();

    /// <summary>Per-linked-object slab state: one contiguous block, bump-allocated. The whole used range is
    /// flipped to RWX at finalize — a dev-loop JIT trades W^X for simplicity, and some sections genuinely
    /// need both (RuntimeDyld places emulated-TLS control blocks, which are mutated at runtime, next to
    /// executable code), so per-section RX/RW protection faults; RWX is the correct pragmatic choice here.</summary>
    private sealed class Slab
    {
        public byte* Base;
        public nuint Offset;

        /// <summary>True when the slab was carved from the shared region (decommitted, not released).</summary>
        public bool InRegion;
    }

    /// <summary>Commits the next slab of the shared region, reserving the region on first use. Falls back to
    /// an independent allocation (the old behavior) once the region is used up.</summary>
    private static byte* AllocateSlab(out bool inRegion)
    {
        lock (RegionLock)
        {
            _region = _region != null
                ? _region
                : (byte*)VirtualAlloc(addr: null, size: RegionSize, type: MEM_RESERVE,
                    protect: PAGE_READWRITE);
            if (_region != null && _regionUsed + SlabSize <= RegionSize)
            {
                void* carved = VirtualAlloc(addr: _region + _regionUsed,
                    size: SlabSize,
                    type: MEM_COMMIT,
                    protect: PAGE_READWRITE);
                if (carved != null)
                {
                    _regionUsed += SlabSize;
                    inRegion = true;
                    return (byte*)carved;
                }
            }
        }

        inRegion = false;
        return (byte*)VirtualAlloc(addr: null,
            size: SlabSize,
            type: MEM_RESERVE | MEM_COMMIT,
            protect: PAGE_READWRITE);
    }

    private static byte* Bump(Slab slab, nuint size, uint alignment)
    {
        nuint align = alignment == 0
            ? 1
            : alignment;
        nuint aligned = slab.Offset + (align - 1) & ~(align - 1);
        if (aligned + size > SlabSize)
        {
            return
                null; // slab exhausted — the module is larger than SlabSize (raise it if this ever trips)
        }

        byte* p = slab.Base + aligned;
        slab.Offset = aligned + size;
        return p;
    }

    // ── memory-manager callbacks (unmanaged entry points) ───────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* CreateContext(void* _)
    {
        try
        {
            byte* baseAddr = AllocateSlab(inRegion: out bool inRegion);
            if (baseAddr == null)
            {
                return null;
            }

            var slab = new Slab { Base = baseAddr, Offset = 0, InRegion = inRegion };
            return (void*)(nint)GCHandle.Alloc(value: slab);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NotifyTerminating(void* _)
    {
        // No cleanup is required when the MCJIT memory manager is notified of termination;
        // the contiguous slab is released in the Destroy callback instead.
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* AllocateCodeSection(void* opaque, nuint size, uint align,
        uint sectionId, sbyte* name)
    {
        try
        {
            var slab = (Slab)GCHandle.FromIntPtr(value: (nint)opaque)
                                     .Target!;
            return Bump(slab: slab, size: size, alignment: align);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* AllocateDataSection(void* opaque, nuint size, uint align,
        uint sectionId, sbyte* name, int isReadOnly)
    {
        try
        {
            var slab = (Slab)GCHandle.FromIntPtr(value: (nint)opaque)
                                     .Target!;
            return Bump(slab: slab, size: size, alignment: align);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FinalizeMemory(void* opaque, sbyte** errMsg)
    {
        try
        {
            var slab = (Slab)GCHandle.FromIntPtr(value: (nint)opaque)
                                     .Target!;
            uint old;
            // DIAGNOSTIC: whole slab RWX to rule out any page-protection fault (data mutated at runtime,
            // e.g. emulated-TLS control blocks). Will tighten to code=RX / data=RW once execution is clean.
            // Return values indicate Win32 success/failure; failure is best-effort here — if protection
            // change fails the JIT will fault on execute, which surfaces as a clear crash rather than silence.
            _ = VirtualProtect(addr: slab.Base,
                size: slab.Offset,
                newProtect: PAGE_EXECUTE_READWRITE,
                oldProtect: &old);
            _ = FlushInstructionCache(process: GetCurrentProcess(),
                addr: slab.Base,
                size: slab.Offset);
            return 0; // LLVMBool: 0 = success
        }
        catch
        {
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Destroy(void* opaque)
    {
        try
        {
            var h = GCHandle.FromIntPtr(value: (nint)opaque);
            if (h.Target is Slab slab && slab.Base != null)
            {
                // Return value indicates Win32 success/failure; failure during teardown is non-recoverable
                // (best-effort release — the OS will reclaim the reservation when the process exits).
                _ = slab.InRegion
                    ? VirtualFree(addr: slab.Base, size: SlabSize, type: MEM_DECOMMIT)
                    : VirtualFree(addr: slab.Base, size: 0, type: MEM_RELEASE);
            }

            h.Free();
        }
        catch
        {
            // best-effort teardown
        }
    }

    // ── object-linking-layer creator (set on the LLJIT builder) ─────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static LLVMOrcOpaqueObjectLayer* CreateObjectLinkingLayer(void* ctx,
        LLVMOrcOpaqueExecutionSession* es, sbyte* triple)
    {
        return LLVM.OrcCreateRTDyldObjectLinkingLayerWithMCJITMemoryManagerLikeCallbacks(ES: es,
            CreateContextCtx: null,
            CreateContext: &CreateContext,
            NotifyTerminating: &NotifyTerminating,
            AllocateCodeSection: &AllocateCodeSection,
            AllocateDataSection: &AllocateDataSection,
            FinalizeMemory: &FinalizeMemory,
            Destroy: &Destroy);
    }

    /// <summary>Installs the contiguous-slab object-linking-layer creator on an LLJIT builder, so the LLJIT
    /// created from it links JIT'd objects into single contiguous slabs (fixing the ADDR32NB flake).</summary>
    public static void InstallOn(LLVMOrcOpaqueLLJITBuilder* builder)
    {
        LLVM.OrcLLJITBuilderSetObjectLinkingLayerCreator(Builder: builder,
            F: &CreateObjectLinkingLayer,
            Ctx: null);
    }
}
