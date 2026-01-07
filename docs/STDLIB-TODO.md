# Standard Library TODO

This document tracks standard library implementation tasks that depend on compiler features being completed first.

## Current Status (Updated 2025-12-19)

✅ **TYPE CONVERSION SYSTEM COMPLETED!**

All empty constructor bodies have been implemented across all numeric type files. The type system now has comprehensive
conversion support:

**Completed (2025-12-19):**

- ✅ **All empty constructors implemented** across sN, uN, fN, dN types
- ✅ **Integer type conversions:** S8, S16, S32, S64, S128, U8, U16, U32, U64, U128
- ✅ **Floating-point conversions:** F16, F32, F64, F128 (all directions)
- ✅ **Decimal conversions:** D32, D64, D128 (all directions)
- ✅ **Cross-category conversions:** integer ↔ float ↔ decimal (all combinations)
- ✅ **Widening conversions** use proper sign/zero extension (sext/zext)
- ✅ **Narrowing conversions** use truncation with overflow checks
- ✅ **Chained conversions** through intermediate types when direct conversion unavailable
- ✅ **S64 checked arithmetic** uncommented and functional (tuple destructuring working)

**Implementation Details:**

- 77 constructor implementations added across 10 files
- All use crashable `!` pattern for overflow/precision-loss conversions
- Proper LLVM intrinsics: sext, zext, fpext, fptrunc, bitcast
- Chaining through intermediate types (e.g., F16 → F32 → F64 → F128)
- Comments added for precision-loss cases (S128 → F32, etc.)

**Build Status:** ✅ Compiles successfully with 0 errors

✅ **MAJOR MILESTONE:** Bug 12.13 (Generic Method Template Matching) is now **FULLY RESOLVED!**

This means:

- Generic types can now be instantiated: `TestType<S64>(value: 42)`
- Generic methods now work: `instance.get_value()` on `TestType<S64>`
- Template matching works automatically: `TestType<S64>` → `TestType<T>`
- **Stdlib generic types are ready for testing!**

Most stdlib code is **architecturally complete** and many features are now **ready for testing** with the working
generics implementation.

---

## 🔄 STDLIB REORGANIZATION: Core Prelude Namespace

### New Organization Structure

**Goal:** Move all fundamental types into the `core` namespace, which is automatically loaded without requiring imports.

All files in `stdlib/core/` should be marked with `namespace Core` and will be available in every RazorForge file
automatically.

### Migration Plan

#### Files to Move into `stdlib/core/` and mark with `namespace Core`:

1. **Primitives** (from `stdlib/NativeDataTypes/`)
    - [ ] S8.rf, S16.rf, S32.rf, S64.rf, S128.rf
    - [ ] U8.rf, U16.rf, U32.rf, U64.rf, U128.rf
    - [ ] SAddr.rf, UAddr.rf
    - [ ] F16.rf, F32.rf, F64.rf, F128.rf
    - [ ] D32.rf, D64.rf, D128.rf
    - [ ] bool.rf

2. **Letters** (now in `stdlib/Text/`)
    - [x] letter.rf (32-bit Unicode codepoint) ✅ Done
    - [x] byte.rf (8-bit UTF-8 code unit) ✅ Done

3. **Error Handling** (from various locations → `stdlib/core/errors/`)
    - [ ] Maybe.rf
    - [ ] Result.rf
    - [ ] Lookup.rf
    - [ ] Crashable.rf
    - [ ] Error.rf

4. **Memory** (from `stdlib/memory/` → `stdlib/core/memory/`)
    - [ ] MemorySize.rf
    - ~~DynamicSlice.rf~~ - **INTERNAL ONLY** (not in core, used by stdlib internals)

5. **CSubsystem** (from `stdlib/CSubsystem/` → `stdlib/core/csubsystem/`)
    - [ ] CChar.rf - single `char` (`@intrinsic.c_char`)
    - [ ] CStr.rf - null-terminated `char*` (has null-termination contract)
    - [ ] CWChar.rf - single `wchar_t` (`@intrinsic.c_wchar`, i16 Win/i32 Unix)
    - [ ] CWStr.rf - null-terminated `wchar_t*` (has null-termination contract)
    - [ ] CVoid.rf - void marker type (use `Snatched<CVoid>` for `void*`)

6. **Utilities** (from `stdlib/core/` → keep in `stdlib/core/`)
    - [ ] BackIndex.rf
    - [ ] Range.rf
    - [ ] Integral.rf

7. **Types** (create new `stdlib/core/types/`)
    - [ ] Blank.rf (unit type)
    - [ ] Duration.rf (time literals)

#### Files to Keep Outside Core (require import):

- **Console.rf** → `stdlib/Console.rf` (namespace Console or implicit)
- **Text/Text.rf** → `stdlib/Text/Text.rf` (namespace Text/Text or implicit)
- **Collections/** → `stdlib/Collections/` (namespace Collections)
- **Integer.rf, Decimal.rf, Fraction.rf** → `stdlib/` (arbitrary precision types)

### New Directory Structure

```
stdlib/
├── core/                        # Always-loaded prelude (namespace: core)
│   ├── primitives/
│   │   ├── S8.rf - S128.rf     (namespace Core)
│   │   ├── U8.rf - U128.rf     (namespace Core)
│   │   ├── F16.rf - F128.rf    (namespace Core)
│   │   ├── D32.rf - D128.rf    (namespace Core)
│   │   ├── SAddr.rf            (namespace Core)
│   │   ├── UAddr.rf            (namespace Core)
│   │   └── Bool.rf             (namespace Core)
│   ├── text/
│   │   ├── Letter.rf           (namespace Core) ✅ Done
│   │   └── Byte.rf             (namespace Core) ✅ Done
│   ├── errors/
│   │   ├── Maybe.rf            (namespace Core)
│   │   ├── Result.rf           (namespace Core)
│   │   ├── Lookup.rf           (namespace Core)
│   │   ├── Crashable.rf        (namespace Core)
│   │   └── Error.rf            (namespace Core)
│   ├── csubsystem/
│   │   ├── CChar.rf            (namespace Core) - single char
│   │   ├── CStr.rf             (namespace Core) - null-terminated char*
│   │   ├── CWChar.rf           (namespace Core) - single wchar_t
│   │   ├── CWStr.rf            (namespace Core) - null-terminated wchar_t*
│   │   └── CVoid.rf            (namespace Core) - void marker
│   ├── memory/
│   │   └── MemorySize.rf       (namespace Core)
│   │   # Note: DynamicSlice.rf is INTERNAL (stdlib/memory/), not in core
│   ├── types/
│   │   ├── Blank.rf            (namespace Core)
│   │   └── Duration.rf         (namespace Core)
│   ├── BackIndex.rf            (namespace Core)
│   ├── Range.rf                (namespace Core)
│   └── Integral.rf             (namespace Core)
├── Console.rf                   # Namespace: Console (or implicit)
├── Text/
│   └── Text.rf                  # Namespace: Text/Text (or implicit)
├── Collections/
│   ├── List.rf                  # Namespace: Collections (or implicit)
│   ├── Dict.rf
│   └── Set.rf
├── Integer.rf
├── Decimal.rf
└── Fraction.rf
```

### Migration Checklist

For each file being moved to `core`:

1. [ ] Move file to appropriate `stdlib/core/` subdirectory
2. [ ] Add `namespace Core` declaration at top of file
3. [ ] **Remove imports for core types** - core types don't need imports
4. [ ] Update imports in other files that reference it
5. [ ] Test that file compiles with namespace declaration
6. [ ] Verify auto-loading when core prelude is implemented

### File Structure Style

**All stdlib files must follow this structure:**

```razorforge
namespace Core

import Collections.List
import Text.Text

# ... declarations ...
```

**Rules:**

1. Namespace first (required for stdlib)
2. One blank line between namespace and imports
3. Imports grouped together
4. One blank line between imports and code
5. **Core modules:** Do NOT import core types (they are always available)

```razorforge
# ✅ GOOD - Core file (no imports for core types)
namespace Core

public record Range<T> {
    start: T
    end: T
}

# ❌ BAD - Core file importing core types
namespace Core

import Core.Integral  # ERROR: Core types don't need import

public record Range<T> {
    start: T
    end: T
}
```

---

## 🔧 STDLIB CODE STYLE UPDATES

### Member Routine Declarations (Outside Type Scope)

**Status:** ✅ **COMPLETED** (2025-12-18) - All stdlib code refactored to use extension method syntax

**Summary:** Successfully refactored all stdlib files to use the correct extension-style method declarations (methods
declared outside type scopes).

**Files Refactored:**

- ✅ **Error handling types:** Maybe.rf, Result.rf, Lookup.rf, DataHandle.rf
- ✅ **Memory wrapper types:** Already correct (Tracked.rf, Retained.rf, Shared.rf, Snatched.rf)
- ✅ **Memory types:** DynamicSlice.rf, MemorySize.rf
- ✅ **Utility types:** BackIndex.rf, Range.rf (already correct)
- ✅ **Native data types:** Blank.rf, None.rf
- ✅ **Error types:** error.rf, common.rf, IndexOutOfBoundsError.rf, DivisionByZeroError.rf, IntegerOverflowError.rf,
  NegativeExponentError.rf, MemoryError.rf, NotImplementedError.rf, IndeterminateResultError.rf
- ✅ **Text/letter types:** Simplified to letter.rf and byte.rf

**Total Files Refactored:** 16 files (out of ~25 surveyed)

**Compilation Status:** ✅ All refactored files compile successfully (`dotnet build` passes)

**Pattern Applied:**

```razorforge
record Point {
    x: F32
    y: F32
}

# Extension-style method declaration
routine Point.distance(other: Point) -> F32 {
    # ...
}
```

**Next Steps:**

- Parser can now safely add restriction to reject in-scope method declarations (COMPILER-TODO.md)
- All new stdlib code should follow this pattern

---

## 1. Core Data Types

### ✅ Completed

**Native Integer Types** (stdlib/NativeDataTypes/)

- S8, S16, S32, S64, S128 (signed)
- U8, U16, U32, U64, U128 (unsigned)
- SAddr, UAddr (platform-dependent)
- All arithmetic, bitwise, shift, and comparison operators
- Checked, wrapping, and saturating variants
- Full intrinsic implementations
- ✅ **All type conversions implemented** (2025-12-19)
- ✅ **S64 checked arithmetic enabled** (2025-12-19)

**Native Float Types** (stdlib/NativeDataTypes/)

- F16, F32, F64, F128 (binary floating point)
- D32, D64, D128 (decimal floating point)
- Basic operators defined
- ✅ **All type conversions implemented** (2025-12-19)

**Boolean Type** (stdlib/NativeDataTypes/bool.rf)

- Logical operators (and, or, not)
- Comparison operators

### ⏳ Waiting on Compiler

**Letter Types** (stdlib/Text/)

- ✅ letter (32-bit Unicode codepoint) - Done
- ✅ byte (8-bit UTF-8 code unit) - Done

---

## 2. Text and String Processing

### ✅ Type System Simplified

**Text Type** (stdlib/Text/Text.rf)

- ✅ Non-generic `Text` entity using `List<letter>` (UTF-32)
- ✅ Non-generic `Bytes` entity using `List<byte>` (UTF-8)
- String operations (concat, slice, search, etc.)
- **Remaining Blockers:**
    - Generic entity instantiation (for List<letter>)
    - `to_text()` protocol implementation
    - `to_bytes()` conversion method

---

## 3. Collections

### ⏳ Waiting on Compiler

**List<T>** (stdlib/Collections/List.rf)

- Dynamic array with growth
- Itertools methods (select, where, fold, etc.)
- **Blockers:**
    - ~~Generic method template matching (Bug 12.13)~~ ✅ FIXED!
    - Generic entity instantiation (two-pass generation)

**Dict<K, V>** (stdlib/Collections/Dict.rf)

- Hash table implementation
- **Blockers:**
    - Generic entity instantiation
    - Multiple type parameters
    - Generic constraints (`K: Hashable`)

**Set<T>** (stdlib/Collections/Set.rf)

- Hash set implementation
- **Blockers:**
    - Generic entity instantiation
    - Generic constraints (`T: Hashable`)

---

## 4. Console I/O

### ✅ Completed (Partial)

**Non-Generic Console Functions** (stdlib/Console.rf)

- `Console.show(value: Bytes)` - print UTF-8 text
- `Console.show_line(value: Bytes)` - print with newline
- `Console.alert(value: Bytes)` - print to stderr
- `Console.alert_line(value: Bytes)` - stderr with newline
- `Console.get_word()`, `Console.get_line()`, etc. - input functions

### ⏳ Waiting on Compiler

**Generic Console Functions** (stdlib/Console.rf - currently commented out)

```razorforge
routine Console.show<T>(value: T) { ... }
routine Console.show_line<T>(value: T) { ... }
routine Console.alert<T>(value: T) { ... }
routine Console.alert_line<T>(value: T) { ... }
```

**Blockers:**

1. Generic function overload resolution (choose between generic/non-generic)
2. Protocol constraints (`<T: Printable>`)
3. `to_text()` protocol implementation for all types
4. `Text.to_bytes()` conversion method

**Impact:** Without this, you can only print `Bytes`. Cannot do `Console.show(42)`.

---

## 5. Error Handling

### ✅ Completed

**Error Types** (stdlib/errors/)

- Base `Error` protocol
- `Crashable` protocol
- Specific errors: `DivisionByZeroError`, `IntegerOverflowError`, `NegativeExponentError`, `IndeterminateResultError`,
  etc.

**Error Handling Types** (stdlib/ErrorHandling/)

- `Maybe<T>` - optional values
- `Result<T>` - operation outcomes with errors
- `Lookup<T>` - query results (found/absent/error)
- `DataState` choice type
- `DataHandle` record

### ⏳ Waiting on Compiler

**Error Type Methods**

- `Maybe<T>.is_valid()`, `is_none()`, etc.
- `Result<T>.is_error()`, etc.
- `Lookup<T>.is_none()`, `is_error()`, etc.
- **Blocker:** Generic method instantiation

**Pattern Matching on Error Types**

```razorforge
when maybe_value {
    is None => handle_missing()
    else v => use(v)
}
```

- **Blocker:** Type pattern matching in when statements

---

## 6. Memory Management

### ✅ Completed

**Memory Wrappers** (stdlib/memory/wrapper/)

- Low-level memory operations
- Pointer arithmetic utilities

### ⏳ Waiting on Compiler

**Smart Pointers**

- `Unique<T>` - unique ownership
- `Shared<T>` - reference counted
- `Weak<T>` - weak references
- **Blocker:** Generic entity instantiation

---

## 7. CSubsystem and Native Interop

### CSubsystem Types (5 types only)

The CSubsystem provides minimal C interop types. For numeric types, use RazorForge types directly (S32, U64, F64, UAddr,
etc.).

| Type     | Meaning                    | Implementation                           |
|----------|----------------------------|------------------------------------------|
| `CChar`  | Single `char`              | `@intrinsic.c_char` (i8)                 |
| `CStr`   | Null-terminated `char*`    | Has null-termination contract            |
| `CWChar` | Single `wchar_t`           | `@intrinsic.c_wchar` (i16 Win, i32 Unix) |
| `CWStr`  | Null-terminated `wchar_t*` | Has null-termination contract            |
| `CVoid`  | Void marker                | Use `Snatched<CVoid>` for `void*`        |

**Pointer relationships:**

- `Snatched<CChar>` = `char*` (raw, no null guarantee)
- `CStr` = `char*` (null-terminated contract)
- `Snatched<CWChar>` = `wchar_t*` (raw, no null guarantee)
- `CWStr` = `wchar_t*` (null-terminated contract)
- `Snatched<CVoid>` = `void*`

**Numeric FFI:** Use RazorForge types directly:
| C Type | RazorForge |
|--------|------------|
| `int32_t` | S32 |
| `int64_t` | S64 |
| `size_t`, `uintptr_t` | UAddr |
| `intptr_t` | SAddr |
| `float` | F32 |
| `double` | F64 |

### ⚠️ Discrepancies Between Wiki and Stdlib

**Wiki (RazorForge-C-Subsystem.md) vs Stdlib (CSubsystem/) comparison:**

| Issue                       | Wiki Says                                      | Stdlib Has                                                                       | Action Needed                                                                        |
|-----------------------------|------------------------------------------------|----------------------------------------------------------------------------------|--------------------------------------------------------------------------------------|
| **CChar missing**           | CChar = single `char` (`@intrinsic.c_char`)    | CSChar (S8) and CUChar (U8) exist                                                | ✅ Create CChar wrapper record (U8), keep CSChar/CUChar as aliases                    |
| **Null pointer constant**   | `C_NULL_PTR`                                   | `C_NULLPTR` (CTypes.rf)                                                          | ✅ Wiki updated to use `C_NULLPTR`                                                    |
| **Extra numeric types**     | "Why no CInt, CLong, etc.?" (should NOT exist) | CInt, CLong, CLongLong, CShort, CFloat, CDouble all exist                        | ✅ Keep CSChar/CUChar/CInt/CLong/CLongLong (+ unsigned), remove CShort/CFloat/CDouble |
| **Namespace inconsistency** | N/A                                            | CStr/CWStr use `namespace CSubsystem`, CTypes uses `namespace CSubsystem/CTypes` | Unify to single namespace                                                            |

**Decisions:**

1. ✅ **CChar, CWChar, CStr, CWStr as wrapper records** - All C character/string types should be wrapper records for
   type safety and consistency:

   ```razorforge
   # CChar.rf - single C char wrapper (U8 for unsigned character semantics)
   record CChar {
       value: U8
   }

   # CWChar.rf - single C wchar_t wrapper (platform-dependent size)
   @config(target: "windows")
   record CWChar {
       value: U16
   }

   @config(target: "unix")
   record CWChar {
       value: U32
   }

   # CStr.rf - already exists as record wrapping UAddr (null-terminated char*)
   # CWStr.rf - already exists as record wrapping UAddr (null-terminated wchar_t*)
   ```

   **Benefits:**
    - Type safety: can't accidentally pass U8 where CChar is expected
    - Consistent design: all C interop types are distinct wrappers
    - Clear semantic meaning for FFI boundaries

2. ✅ **Keep essential C integer types** - These are commonly used in C FFI:
    - CSChar, CUChar (S8/U8) - for explicit `signed char` / `unsigned char`
    - CInt, CUInt (S32/U32) - ubiquitous in C APIs
    - CLong, CULong (S32/U32 Windows, S64/U64 Unix) - platform-dependent, genuinely useful
    - CLongLong, CULongLong (S64/U64) - common for 64-bit in C
    - **Remove:** CShort, CUShort (use S16/U16), CFloat, CDouble (use F32/F64)

3. ✅ **Null pointer constant**: Use `C_NULLPTR` (follows C++ naming convention)

### 📋 TODO: CSubsystem Cleanup

- [x] CChar = alias for Byte (deleted CChar.rf, added `define CChar as Byte` in CTypes.rf)
- [x] Create `CWChar.rf` as wrapper record (platform-dependent U16/U32)
- [x] Remove: CShort, CUShort, CFloat, CDouble (use S16/U16/F32/F64 directly)
- [x] Keep: CSChar, CUChar, CInt, CUInt, CLong, CULong, CLongLong, CULongLong
- [x] Unify namespace to `CSubsystem` across all files
- [x] Update wiki to use `C_NULLPTR`

### ✅ Completed

**External C Functions** (stdlib/Console.rf, stdlib/memory/)

- `rf_console_print_cstr`, `rf_console_get_line`, etc.
- `rf_memory_alloc`, `rf_memory_free`, etc.
- **Note:** Need C runtime implementation (see runtime.c TODO)

### ⏳ Waiting on Parser

**Imported Declarations**

```razorforge
imported("C") routine printf(format: CStr, ...) -> S32
imported("C") routine malloc(size: UAddr) -> Snatched<CVoid>
```

- **Blocker:** `imported` keyword parsing
- **Blocker:** Variadic `...` parameter parsing

**Note:** The `external` keyword was **RENAMED** to `imported` for FFI declarations.

---

## 8. Protocols and Traits

### ⏳ Waiting on Compiler

**Protocol Declarations**

```razorforge
protocol Printable {
    routine to_text() -> Text
}

protocol Hashable {
    routine hash() -> U64
}

protocol Equatable {
    routine __eq__(you: Me) -> Bool
}

protocol Comparable follows Equatable {
    routine __cmp__(you: Me) -> ComparisonSign
}
```

**Blockers:**

- `protocol` keyword parsing
- Protocol implementation checking
- Generic constraints (`<T: Comparable>`)

### 📋 TODO: Auto-Generated Comparison Operators

**ComparisonSign choice type (stdlib/Core/ComparisonSign.rf):**

```razorforge
choice ComparisonSign {
    ME_SMALL: -1   # me < you
    SAME: 0        # me == you
    ME_LARGE: 1    # me > you
}
```

**Design:** Only `__eq__` and `__cmp__` need manual implementation. The compiler auto-generates the rest:

| Operator | Generated From             |
|----------|----------------------------|
| `__ne__` | `not __eq__(you)`          |
| `__lt__` | `__cmp__(you) == ME_SMALL` |
| `__le__` | `__cmp__(you) != ME_LARGE` |
| `__gt__` | `__cmp__(you) == ME_LARGE` |
| `__ge__` | `__cmp__(you) != ME_SMALL` |

**User can override:**

- `__eq__` - custom equality logic
- `__cmp__` - custom ordering logic (returns `ComparisonSign`)

**User cannot override** (always generated):

- `__ne__`, `__lt__`, `__le__`, `__gt__`, `__ge__`

**Rationale:** Prevents inconsistent comparison implementations (e.g., `a < b` and `a >= b` both returning true).

---

## 9. Ranges and Iteration

### ⏳ Waiting on Compiler

**Range<T>** (stdlib/core/Range.rf)

- `0 to 10`, `10 downto 0`
- Iterator protocol
- **Blocker:** Generic record instantiation

**BackIndex<I>** (stdlib/core/BackIndex.rf)

- Backward indexing: `^1`, `^2`
- **Blockers:**
    - ~~Bug 12.13 (method template matching)~~ ✅ FIXED!
    - Generic record instantiation (two-pass generation)

---

## 10. Sequences and Seqtools

### ⏳ Waiting on Compiler

**Sequence Protocol**

```razorforge
protocol Sequence<T> {
    routine next() -> T?
    routine has_next() -> bool
}
```

**Seqtools Functions**

- Already defined on `List<T>` but cannot be tested
- `select<U>`, `where`, `fold<U>`, etc.
- **Blockers:**
    - Generic entity methods
    - Lambda code generation
    - Higher-order functions

---

## 11. Suflae Runtime (Actor Model)

### ⏳ Waiting on Compiler (Phase 9)

**Green Thread Runtime** (stdlib/Runtime/GreenThread/)

The Suflae actor model requires a green thread runtime for lightweight concurrency. This is **Suflae-only** - RazorForge
uses explicit primitives.

| Component      | File            | Description                                |
|----------------|-----------------|--------------------------------------------|
| `GreenThread`  | GreenThread.sf  | Lightweight coroutine/fiber (~2-4KB stack) |
| `Scheduler`    | Scheduler.sf    | Work-stealing scheduler across CPU cores   |
| `Channel<T>`   | Channel.sf      | Bounded MPSC channel for actor mailboxes   |
| `ActorRuntime` | ActorRuntime.sf | Actor lifecycle management                 |
| `Message`      | Message.sf      | Serialized method call representation      |

**Directory Structure:**

```
stdlib/Runtime/
├── GreenThread/
│   ├── GreenThread.sf      # Fiber/coroutine implementation
│   ├── Scheduler.sf        # Work-stealing scheduler
│   ├── Channel.sf          # Bounded MPSC channel
│   ├── ActorRuntime.sf     # Actor spawn/lifecycle
│   └── Message.sf          # Method call serialization
└── Reflection/             # (Future: runtime type info)
```

**Implementation Notes:**

```
.share() compiles to:
1. Allocate bounded Channel<Message>
2. Spawn GreenThread running actor loop
3. Return Shared<T> handle with channel reference

Method call on Shared<T> compiles to:
1. Serialize arguments into Message
2. Send to actor's channel (blocks if full - backpressure)
3. For void methods: return immediately (fire-and-forget)
4. For returning methods: wait on reply channel

Actor loop (runs in green thread):
loop:
    let msg = channel.receive()  # Blocks until message
    let result = dispatch(msg)   # Call actual method
    if msg.has_reply_channel:
        msg.reply_channel.send(result)
```

**Blockers:**

- Phase 9 in COMPILER-TODO.md (Suflae actor model semantic analysis + codegen)
- Generic entities (`Channel<T>`)
- Coroutine/fiber primitives in native runtime
- Work-stealing scheduler implementation

**Future: Supervision Trees**

```suflae
# Not yet designed - Erlang/Elixir-style fault tolerance
let supervisor = Supervisor(
    strategy: ONE_FOR_ONE,
    children: [worker1.share(), worker2.share()]
)
```

---

## Implementation Priority

### Phase 1: After Generic Types Work

1. Enable generic Console functions (critical for usability)
2. Test `Text` and `Bytes` implementation
3. Test `List<T>` with simple types (`List<S32>`)

### Phase 2: After Protocol System

1. Implement `to_text()` for all primitive types
2. Implement `Hashable` for primitives
3. Test `Dict<K, V>` and `Set<T>`

### Phase 3: Advanced Features

1. Smart pointers (`Unique<T>`, `Shared<T>`)
2. Advanced itertools with lambdas
3. String interpolation

---

## Testing Status

**Current State:** Most stdlib code compiles (parses and analyzes) but cannot be executed or tested due to missing
compiler features.

**Next Steps:**

1. ~~Fix Bug 12.13 (generic method template matching)~~ ✅ COMPLETED (2025-12-04)
2. ✅ Text type system simplified (letter/byte, Text/Bytes) - DONE
3. **Test simple generic types** (Range<S64>, TestType<S64>, etc.) - NOW POSSIBLE!
4. Implement generic record instantiation properly (two-pass generation)
5. Enable and test `Console.show<T>` (requires overload resolution)
6. Test `List<T>` with basic operations
7. Expand testing to other generic types

---

## Dependencies on Compiler Features

| Stdlib Feature            | Compiler Requirement                         | Status           |
|---------------------------|----------------------------------------------|------------------|
| Text type system          | Non-generic Text/Bytes with letter/byte      | ✅ Done!          |
| Generic Console functions | Overload resolution + constraints            | ⏳ Waiting        |
| Text methods              | ~~Generic method instantiation (Bug 12.13)~~ | ✅ READY!         |
| List<T> operations        | Generic entity instantiation + methods       | ✅ Methods ready! |
| Dict<K, V>                | Multiple type params + constraints           | ⏳ Waiting        |
| Protocol implementations  | Protocol system + constraint checking        | ⏳ Waiting        |
| Smart pointers            | Generic entities + drop semantics            | ⏳ Waiting        |
| Itertools with lambdas    | Lambda code generation                       | ⏳ Waiting        |
| Suflae actor runtime      | Phase 9 (actor model) + green threads        | ⏳ Waiting        |

**See:** [COMPILER-TODO.md](COMPILER-TODO.md) for compiler feature roadmap.
