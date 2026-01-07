# RazorForge Compiler TODO

## Quick Status

| Phase             | Status     | Description                                                   |
|-------------------|------------|---------------------------------------------------------------|
| Lexer/Parser      | ✅ COMPLETE | Stable, synchronized between RazorForge and Suflae            |
| Semantic Analyzer | ✅ COMPLETE | Type system, scopes, mutation inference, error variants       |
| Code Generator    | ✅ WORKING  | Stdlib intrinsics compile to native LLVM, end-to-end verified |
| LSP               | ⏳ TODO     | Will reuse semantic analyzer infrastructure                   |
| Suflae Transpiler | ⏳ TODO     | GC + Reflection + Actor insertion                             |

---

## Recent Session: Stdlib List.rf Uses Native Memory

**Date:** January 7, 2026

### Milestone: List<T> Uses Snatched<T> and Native Heap Functions

Updated `stdlib/Collections/List.rf` to use proper native memory management:

### Changes Made

1. **Native Memory Functions** - List.rf now uses core heap functions:
    - `heap_alloc(bytes: UAddr) -> UAddr` - Allocate heap memory
    - `heap_free(address: UAddr)` - Free heap memory
    - `heap_realloc(address: UAddr, new_bytes: UAddr) -> UAddr` - Resize allocation
    - `data_size<T>()` - Compiler intrinsic for type size in bytes

2. **Snatched<T> Pointer Wrapper** - Heap-allocated data uses `Snatched<T>`:
    - `.read()` - Read value at pointer
    - `.write(value)` - Write value at pointer
    - `.offset(bytes)` - Get offset pointer
    - `.is_none()` - Check for null
    - `snatched_none<T>()` - Create null pointer

3. **Indexing Overloads** - Added multiple `__getitem__`/`__setitem__` overloads:
    - `Integral` types (forward index)
    - `BackIndex` types (backward index using `^`)
    - `Range<Integral>` (forward range slicing)
    - `Range<BackIndex>` (backward range slicing)

### Example Implementation

```razorforge
routine List<T>.add_last(me: List<T>, value: T) {
    if me.count >= me.capacity {
        let new_capacity = if me.capacity == 0u64 then 4u64 else me.capacity * 2u64
        me.reserve(new_capacity)
    }
    let byte_offset = me.count * data_size<T>()
    me.data.offset(byte_offset).write(value)
    me.count = me.count + 1u64
}
```

### Files Modified

- `stdlib/Collections/List.rf` - Complete rewrite using native memory

---

## Previous Session: Stdlib Intrinsics Now Compile to Native LLVM

**Date:** January 7, 2026

### Milestone: Removed primitives.c Dependency

The codegen now compiles stdlib routines with `@intrinsic.*` API, emitting native LLVM instructions instead of calling
external C functions.

### Changes Made

1. **Stdlib Program Compilation** - Codegen now receives and compiles stdlib ASTs
    - `LLVMCodeGenerator` accepts `stdlibPrograms` parameter
    - Stdlib routine bodies are compiled with their intrinsic operations
    - Example: `S64.__add__` now emits `add i64` instruction directly

2. **LLVM Type Name Aliases** - Added aliases for stdlib convenience
    - `@intrinsic.double` → `@intrinsic.f64`
    - `@intrinsic.float` → `@intrinsic.f32`
    - `@intrinsic.half` → `@intrinsic.f16`
    - `@intrinsic.fp128` → `@intrinsic.f128`

3. **Single-Field Wrapper Fix** - F64, F32, etc. now correctly unwrap
    - Before: `%Record.F64 = type { }` (empty struct)
    - After: `double` (correct LLVM type)

4. **Function Deduplication** - Prevents declare/define conflicts
    - `_generatedFunctions` and `_generatedFunctionDefs` tracking
    - Still has minor issues with stdlib routines that have parse errors

### Generated LLVM Example

```llvm
define i64 @S64___add__(i64 %me, i64 %you) {
entry:
  %you.addr = alloca i64
  store i64 %you, ptr %you.addr
  %tmp498 = load i64, ptr %you.addr
  %tmp499 = add i64 %me, %tmp498
  ret i64 %tmp499
}
```

### Remaining Issues

1. **Duplicate function declarations** - Some stdlib routines with parse errors cause both `declare` and `define` to be
   emitted
2. ~~**Stdlib parse errors** - Console.rf, Decimal.rf, Integer.rf, Blank.rf have syntax issues~~ **FIXED** (2026-01-07)
    - Added `where` keyword support for generic constraints
    - Added `private` field support inside entity bodies
    - Added `PassDeclaration` for `pass` inside type bodies

### Files Modified

- `src/CodeGen/LLVMCodeGenerator.cs` - Added stdlib programs support
- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - Function deduplication
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Fixed MemberExpression property access
- `src/Analysis/TypeRegistry.cs` - Added LLVM type name aliases

---

## Previous Session: End-to-End Execution Working!

**Date:** January 6, 2026

### Milestone: First Successful Native Execution

Successfully compiled and ran RazorForge code end-to-end:

```
$ ./test_codegen.exe
Testing RazorForge codegen:
  factorial(5) = 120
  add_s64(10, 20) = 30
  max_s64(15, 8) = 15
```

### Changes Made

1. **Function Name Mangling** - Changed from `Type.method` to `Type_method` for C ABI compatibility
    - `LLVMCodeGenerator.Declarations.cs:469` - MangleFunctionName uses `_` separator
    - `LLVMCodeGenerator.Expressions.cs:548` - EmitMethodCall fallback uses `_`

2. **Runtime Primitive Stubs** - Created `native/runtime/primitives.c`
    - S64: `__add__`, `__sub__`, `__mul__`, `__div__`, `__rem__`, `__eq__`, `__ne__`, `__lt__`, `__le__`, `__gt__`,
      `__ge__`
    - S32: Same operations
    - F64/F32: Same operations (excluding `__rem__`)
    - Bool: `__and__`, `__or__`, `__not__`, `__eq__`, `__ne__`

3. **Main Wrapper** - Created `native/runtime/main_wrapper.c`
    - Calls RazorForge `start()` entry point
    - Provides C `main()` for executable linking

### Build Command

```bash
dotnet run -- codegen test_codegen.rf
clang test_codegen.ll primitives.o native/runtime/main_wrapper.c -o test_codegen.exe
./test_codegen.exe
```

### Verified Features

| Feature                | Status                             |
|------------------------|------------------------------------|
| Arithmetic (`+ - *`)   | ✅ Executes correctly               |
| Comparison (`> == <=`) | ✅ Executes correctly               |
| If/else control flow   | ✅ Executes correctly               |
| Recursion (factorial)  | ✅ `factorial(5) = 120`             |
| Function calls         | ✅ Parameters passed correctly      |
| Variable declarations  | ✅ Allocas and stores work          |
| Float operations (F64) | ✅ Compiles (not tested at runtime) |

### Next Steps

1. **Expand test coverage:**
    - [ ] Loops (while, for, until)
    - [ ] Pattern matching (when)
    - [ ] Records and entities
    - [ ] Collections (List, Dict)

2. **Runtime expansion:**
    - [ ] Memory allocation (`rf_alloc`, `rf_free`)
    - [ ] I/O operations (`show`, `show_line`, `get_line`)
    - [ ] Text/String handling

3. **Optimization:**
    - [ ] Inline primitive operations instead of function calls
    - [ ] Dead code elimination
    - [ ] Constant folding

---

## Architecture Overview

```
Suflae source (.sf)                    RazorForge source (.rf)
       ↓                                        ↓
   Suflae Lexer                           RF Lexer
       ↓                                        ↓
   Suflae Parser                          RF Parser
       ↓                                        ↓
   Suflae AST                              RF AST
       ↓                                        │
   Transpiler ──────────────────────────────────┤
   (GC + Reflection + Actor insertion)          │
       ↓                                        ↓
       └──────────────→ RazorForge AST ←────────┘
                              ↓
                    Semantic Analyzer
                    (type checking, generic instantiation,
                     symbol resolution)
                              ↓
                        Typed AST / IR
                        ↙         ↘
                LLVM CodeGen      LSP Services
```

---

## Completed Work Summary

### Semantic Analyzer (Phases 1-6) ✅ COMPLETE

- **Type System**: TypeRegistry, TypeInfo hierarchy (Record/Entity/Resident/Choice/Variant/Mutant/Protocol)
- **Scope System**: Hierarchical scopes with ScopeKind (Global/Module/Type/Function/Block)
- **Statement Analysis**: Variable declarations, assignments, control flow, loops, returns
- **Expression Analysis**: Identifiers, operators, calls, member access, patterns
- **Mutation Inference**: Call graph propagation for me mutation tracking
- **Error Handling**: Auto-generation of try_/check_/find_ variants from `!` functions
- **Derived Operators**: Auto-generate `__ne__` from `__eq__`, comparison ops from `__cmp__`

### Code Generator (Phase 7) 🔄 IN PROGRESS

**Completed:**

- Expressions: intrinsics, calls, operators, literals, conditionals, indexing
- Statements: variables, assignments, returns, if/else, loops, when/pattern matching
- Terminator tracking for valid LLVM IR basic blocks
- String constant deduplication
- Native function declaration tracking

**Remaining:**

- [x] End-to-end LLVM verification and execution ✅ January 6, 2026
- [ ] Loops (while, for, until) - needs testing
- [ ] Collections (List, Dict, Set)
- [ ] Text types (Text, Bytes, TextBuffer)
- [ ] I/O operations (show, show_line, get_line)
- [ ] FFI (imported routines)
- [ ] Concurrency (Shared, inspecting/seizing)
- [ ] Runtime initialization

---

## Source Organization

```
src/Analysis/
├── Enums/           # Language, TypeCategory, MutationCategory, ScopeKind, etc.
├── Results/         # AnalysisResult, SemanticError, SemanticWarning
├── Symbols/         # RoutineInfo, VariableInfo, FieldInfo, ParameterInfo
├── Types/           # TypeInfo hierarchy (15+ type classes)
├── Scopes/          # Scope hierarchy
├── Inference/       # CallGraph, MutationInference, ErrorHandlingGenerator
├── Modules/         # ModuleResolver, CompilationDriver, StdlibLoader
├── Native/          # NumericLiteralParser
├── TypeRegistry.cs  # Central type repository
└── SemanticAnalyzer.*.cs  # Partial classes for analysis phases

src/CodeGen/
├── LLVMCodeGenerator.cs            # Main infrastructure
├── LLVMCodeGenerator.Types.cs      # Type generation
├── LLVMCodeGenerator.Declarations.cs  # Function declarations
├── LLVMCodeGenerator.Statements.cs # Statement emission
└── LLVMCodeGenerator.Expressions.cs # Expression emission
```

---

## Future Phases

### Phase 8: `becomes` Keyword for Block Results ✅

- Add `becomes` to lexer (TokenType.Becomes) ✅
- Parser support for `becomes expression` in block contexts ✅
- Semantic analysis for `becomes` statement ✅
- Works in `when` branches, `if` expressions, any block that produces a value
- TODO: CE if trailing value without `becomes` in multi-statement blocks

### Phase 9: LSP Integration

- Reuse TypeRegistry for hover, completions, go-to-definition
- Incremental parsing/analysis

### Phase 10: Suflae Actor Model

- `.share()` → actor infrastructure
- Method calls → message sends
- GreenThread scheduler

### Phase 11: Generator Routines (`generate`)

- State machine transformation
- Coroutine frame allocation

### Phase 12: Async Routines (`suspended`, `waitfor`)

- Async/await state machines
- Task executor integration

---

## Build Status

| Component          | Status                                |
|--------------------|---------------------------------------|
| C# Compiler        | ✅ Builds (warnings only)              |
| Native Runtime     | ✅ primitives.c + main_wrapper.c       |
| LLVM IR Generation | ✅ Generates valid IR                  |
| LLVM Compilation   | ✅ Links and executes                  |
| End-to-End Test    | ✅ factorial(5)=120, add_s64(10,20)=30 |

---

## Key Design Decisions

1. **No user-visible primitives** - S32, Bool, etc. are single-field records wrapping `@intrinsic.*`
2. **Operators are methods** - `a + b` → `a.__add__(b)` at parse time
3. **Types**: record (value), entity (reference), resident (fixed reference), choice, variant, mutant, protocol
4. **Error handling**: `!` suffix, `throw`/`absent`, auto-generated safe variants
5. **Memory wrappers**: Handles (Shared, Tracked) vs Tokens (Viewed, Hijacked, Inspected, Seized)
