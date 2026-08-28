#!/usr/bin/env bash
# RazorForge packaging script (Unix host -> host-RID package).
#
# Produces: dist/razorforge-v<version>-<rid>.tar.gz (+ sha256 in checksums.txt)
# where <rid> is the host's RID: linux-x64, osx-arm64, or osx-x64.
#
# IMPORTANT: packages the HOST platform only. The native runtime (razorforge_runtime)
# is built by the host toolchain, and `dotnet publish -r <foreign-rid>` would silently
# bundle the host's native binaries into a foreign-platform archive. Build the Windows
# package on Windows with scripts/package.ps1.
#
# Prerequisites: .NET 10 SDK; clang + cmake + ninja (native runtime build); the
# vendored native sources under native/ (CI fetches them; dev checkouts have them).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

case "$(uname -s)" in
    Linux)  RID=linux-x64 ;;
    Darwin) if [[ "$(uname -m)" == "arm64" ]]; then RID=osx-arm64; else RID=osx-x64; fi ;;
    *)
        echo "package.sh packages Linux/macOS hosts; use scripts/package.ps1 on Windows." >&2
        exit 1
        ;;
esac
VERSION="${VERSION:-$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' RazorForge.csproj | head -1)}"
NAME="razorforge-v${VERSION}-${RID}"
OUT="dist/${NAME}"

echo "=== RazorForge ${VERSION} -> ${NAME} ==="
rm -rf "$OUT" "dist/${NAME}.tar.gz"
mkdir -p dist

echo "=== build native runtime first ==="
# The csproj's Content globs for native/build/bin|lib are evaluated BEFORE the
# native build runs during publish — on a fresh checkout publish would silently
# ship no native artifacts. Build them up front so the globs see real files.
(cd native && bash build.sh)

echo "=== dotnet publish (self-contained) ==="
dotnet publish RazorForge.csproj -c Release -r "$RID" --self-contained true -o "$OUT" \
    --verbosity minimal

echo "=== flatten native runtime artifacts (installed layout) ==="
# `dotnet publish` keeps Content items under their source-relative paths
# (native/build/bin|lib). The installed layout — and the compiler's own P/Invoke
# probing + buildandrun's prebuilt-layout detection — expect them FLAT next to
# the executable, matching the dev bin/ output.
for sub in native/build/bin native/build/lib; do
    if [ -d "$OUT/$sub" ]; then
        mv "$OUT/$sub"/* "$OUT/" 2>/dev/null || true
    fi
done
rm -rf "$OUT/native"

echo "=== prune dev-only artifacts ==="
rm -rf "$OUT/RazorForge-Wiki" "$OUT/Suflae-Wiki"
find "$OUT" -name '*.pdb' -delete
cp LICENSE README.md "$OUT/"

# Short alias next to the canonical binary. (Users preferring `forge` can alias it
# themselves — the name is deliberately not shipped to avoid colliding with
# Foundry's `forge`.)
ln -sf RazorForge "$OUT/rf"

# Suflae aliases. COPIES, not symlinks: the binary keys its Suflae branding + default
# language off its own invoked name (Environment.ProcessPath), and on Linux ProcessPath
# resolves a symlink back to RazorForge — a copy preserves the `suflae`/`sf` name. The
# apphost stub locates RazorForge.dll by name in its own directory, so the renamed copy
# still launches the same compiler.
cp "$OUT/RazorForge" "$OUT/suflae"
cp "$OUT/RazorForge" "$OUT/sf"

echo "=== bundle self-contained LLVM toolchain ==="
# The compiler resolves <package>/toolchain/bin before PATH (ResolveToolchainTool),
# and uses that toolchain's ld.lld/ld64.lld for linking. Linux still needs the
# host's libc dev files (crt1.o); macOS needs the Command Line Tools SDK stubs —
# both documented in QUICKSTART.md.
LLVM_VERSION=22.1.7
CACHE=dist/_cache
mkdir -p "$CACHE"
case "$RID" in
    linux-x64)  LLVM_ASSET="LLVM-${LLVM_VERSION}-Linux-X64" ;;
    osx-arm64)  LLVM_ASSET="LLVM-${LLVM_VERSION}-macOS-ARM64" ;;
    *)          LLVM_ASSET="" ;;
esac
if [[ -n "$LLVM_ASSET" ]]; then
    TARBALL="$CACHE/${LLVM_ASSET}.tar.xz"
    if [[ ! -f "$TARBALL" ]]; then
        curl -L -o "$TARBALL" "https://github.com/llvm/llvm-project/releases/download/llvmorg-${LLVM_VERSION}/${LLVM_ASSET}.tar.xz"
    fi
    LLVM_ROOT="$CACHE/${LLVM_ASSET}"
    if [[ ! -d "$LLVM_ROOT/bin" ]]; then
        tar -xf "$TARBALL" -C "$CACHE"
    fi

    TC="$OUT/toolchain"
    mkdir -p "$TC/bin" "$TC/lib/clang"
    # -L: follow symlinks so `clang` is the real binary, not a dangling link.
    for tool in clang opt lld; do
        cp -L "$LLVM_ROOT/bin/$tool" "$TC/bin/"
    done
    # lld dispatches on argv[0]; clang looks for ld.lld (ELF) / ld64.lld (Mach-O)
    # next to itself when given -fuse-ld=lld.
    (cd "$TC/bin" && ln -sf lld ld.lld && ln -sf lld ld64.lld)
    # Shared libraries the tools load (no-ops for static builds).
    cp -a "$LLVM_ROOT"/lib/libLLVM*.so* "$TC/lib/" 2>/dev/null || true
    cp -a "$LLVM_ROOT"/lib/libclang-cpp*.so* "$TC/lib/" 2>/dev/null || true
    cp -a "$LLVM_ROOT"/lib/libLLVM*.dylib "$TC/lib/" 2>/dev/null || true
    cp -a "$LLVM_ROOT"/lib/libclang-cpp*.dylib "$TC/lib/" 2>/dev/null || true

    # The official Linux LLVM binaries are not fully static. Carry every
    # non-baseline dependency reported by ldd beside the bundled LLVM libraries
    # and make package creation fail if any dependency remains unresolved.
    if [[ "$RID" == "linux-x64" ]]; then
        is_baseline_linux_lib() {
            case "$(basename "$1")" in
                linux-vdso.so.*|ld-linux-x86-64.so.*|libc.so.*|libm.so.*|libpthread.so.*|libdl.so.*|librt.so.*|libresolv.so.*)
                    return 0
                    ;;
                *)
                    return 1
                    ;;
            esac
        }

        copy_linux_tool_deps() {
            local tool="$1"
            LD_LIBRARY_PATH="$LLVM_ROOT/lib:$TC/lib:${LD_LIBRARY_PATH:-}" ldd "$tool" |
                awk '
                    /=>/ && $3 ~ /^\// { print $3 }
                    /^[[:space:]]*\// { print $1 }
                ' |
                while read -r dep; do
                    [[ -n "$dep" && -e "$dep" ]] || continue
                    if is_baseline_linux_lib "$dep"; then
                        continue
                    fi
                    cp -L "$dep" "$TC/lib/$(basename "$dep")"
                done
        }

        for tool in clang opt lld; do
            copy_linux_tool_deps "$LLVM_ROOT/bin/$tool"
        done
    fi
    # clang resource dir: compiler-rt builtins (--rtlib=compiler-rt) live here.
    cp -a "$LLVM_ROOT/lib/clang/${LLVM_VERSION%%.*}" "$TC/lib/clang/"
    find "$TC/lib/clang" -type d -name include -exec rm -rf {} + 2>/dev/null || true

    if [[ "$RID" == "linux-x64" ]]; then
        for tool in "$TC/bin/clang" "$TC/bin/opt" "$TC/bin/lld" "$TC/bin/ld.lld"; do
            if LD_LIBRARY_PATH="$TC/lib:${LD_LIBRARY_PATH:-}" ldd "$tool" | grep -q "not found"; then
                LD_LIBRARY_PATH="$TC/lib:${LD_LIBRARY_PATH:-}" ldd "$tool" >&2 || true
                echo "Bundled LLVM tool has unresolved shared-library dependencies: $tool" >&2
                exit 1
            fi
        done
    fi
fi

echo "=== add install script + quickstart + AI reference ==="
cp scripts/package-assets/install.sh scripts/package-assets/QUICKSTART.md RAZORFORGE-FOR-AI.md "$OUT/"
chmod +x "$OUT/install.sh"

echo "=== smoke test: self-contained buildandrun (system toolchain hidden) ==="
"$OUT/RazorForge" version
RF_ABS="$(cd "$OUT" && pwd)/RazorForge"
SMOKE_DIR=$(mktemp -d)
cat > "$SMOKE_DIR/smoke.rf" <<'EOF'
module PackageSmoke

import IO/Console

routine start()
  show("packaged razorforge works")
  return
EOF
# Stage a copy of the package OUTSIDE the repo (buildandrun's dev-checkout
# detection walks up from the exe and would find the repo's native/build tree
# from dist/), then run from the smoke dir with a minimal PATH — the end-user
# situation. The bundled toolchain must carry the build; only the OS linker
# prerequisites (libc dev files / CLT) come from the system.
STAGE_DIR=$(mktemp -d)
cp -a "$OUT/." "$STAGE_DIR/"
RF_ABS="$STAGE_DIR/RazorForge"
SMOKE_OUT=$( cd "$SMOKE_DIR" && PATH="/usr/bin:/bin" "$RF_ABS" buildandrun smoke.rf 2>&1 ) || {
    echo "$SMOKE_OUT"
    echo "self-contained buildandrun smoke failed"
    rm -rf "$STAGE_DIR"
    exit 1
}
rm -rf "$STAGE_DIR"
if ! grep -q "packaged razorforge works" <<< "$SMOKE_OUT"; then
    echo "$SMOKE_OUT"
    echo "self-contained buildandrun smoke failed (wrong output)"
    exit 1
fi
echo "self-contained buildandrun OK"
rm -rf "$SMOKE_DIR"

echo "=== archive + checksum ==="
tar -C dist -czf "dist/${NAME}.tar.gz" "$NAME"
# macOS has no sha256sum; shasum -a 256 emits the same format.
if command -v sha256sum >/dev/null 2>&1; then
    (cd dist && sha256sum "${NAME}.tar.gz" >> checksums.txt)
else
    (cd dist && shasum -a 256 "${NAME}.tar.gz" >> checksums.txt)
fi
echo "Packaged: dist/${NAME}.tar.gz"
