# RazorForge Compiler TODO

**Last Updated:** 2025-12-19

This document tracks compiler features needed for RazorForge and Suflae. For standard library implementation tasks,
see [STDLIB-TODO.md](STDLIB-TODO.md).

---

## PARSER TERMINOLOGY UPDATES NEEDED

**Status:** ⚠️ **IN PROGRESS** - RazorForge parser updated, Suflae parser needs updates

The following terminology updates have been applied to the RazorForge parser and need to be applied to the Suflae parser as well:

**Completed in RazorForge Parser (2025-12-19):**
- ✅ "function" → "routine" in all error messages and comments
- ✅ "enum" → "choice" in all error messages
- ✅ "external" → "imported" for FFI function comments
- ✅ "feature" → "protocol" in method names and error messages
- ✅ "observing" → "inspecting" in method names
- ✅ `ParseFeatureDeclaration` → `ParseProtocolDeclaration`
- ✅ `ParseObservingStatement` → `ParseInspectingStatement`
- ✅ Access modifier comments updated to: private, family, internal, public
- ✅ Added descriptive comments for type declarations

**TODO - Apply to Suflae Parser:**
- [ ] Update all "function" → "routine" in error messages in `src/Parser/SuflaeParser*.cs`
- [ ] Update all "enum" → "choice" in error messages
- [ ] Update "external" → "imported" comments for FFI functions
- [ ] Update "feature" → "protocol" if applicable to Suflae syntax
- [ ] Update "observing" → "inspecting" if applicable to Suflae syntax
- [ ] Rename any `ParseFeatureDeclaration` → `ParseProtocolDeclaration` methods
- [ ] Rename any `ParseObservingStatement` → `ParseInspectingStatement` methods
- [ ] Update access modifier documentation
- [ ] Update type declaration comments with proper terminology

**TODO - AST Consistency:**
- [ ] Rename `ExternalDeclaration` → `ImportedDeclaration` in `src/AST/Declarations.cs` (line 695)
  - Currently inconsistent: class is `ExternalDeclaration` but visitor method is `VisitImportedDeclaration`
  - Update all references in parsers, code generators, and semantic analyzers

**Files to Update:**
- `src/Parser/SuflaeParser.cs`
- `src/Parser/SuflaeParser.Declarations.cs`
- `src/Parser/SuflaeParser.Statements.cs`
- `src/Parser/SuflaeParser.Expressions.cs`
- `src/Parser/SuflaeParser.Types.cs`
- `src/Parser/SuflaeParser.Helpers.cs`

---

## CRITICAL PARSE/SEMANTIC ERRORS (BLOCKING STDLIB)

### Parser - Missing Keywords and Syntax

**Status:** 🔴 **BLOCKING** - Multiple stdlib files fail to parse

The following features are documented/used in stdlib but not implemented in the parser:

**1. Missing Keywords:**
- ✅ `follows` - Protocol implementation (IMPLEMENTED 2025-12-18)
- `resident` - Permanent fixed-size reference types in persistent memory space (used in: Collections/*.rf)
- `where` - Generic constraints (used in: FixedDeque.rf)
- ✅ `isnot` - Negated type check operator (IMPLEMENTED 2025-12-18)
- ✅ `notfrom` - Negated inheritance check operator (IMPLEMENTED 2025-12-18)
- ✅ `notfollows` - Negated protocol check operator (IMPLEMENTED 2025-12-18)
- ✅ `pass` - No-op statement (IMPLEMENTED 2025-12-18)

**2. Missing Generic Constraint Syntax:**
```razorforge
# Current syntax tries to parse this:
entity Iterator<T> {
    routine next!() -> T where T: Hashable  # ❌ "where" not recognized
}

# Also used in collections:
routine compare<T>(a: T, b: T) -> bool where T: Comparable
```

**3. Missing Protocol Abstract Method Syntax:**
```razorforge
# Crashable.rf fails to parse:
protocol Crashable {
    @readonly routine Me.crash_message() -> Text<letter32>  # ❌ Expected field, got routine
}
```

**4. Missing Intrinsic Function Body Syntax:**
```razorforge
# compilerservice.rf fails to parse:
@intrinsic(compiler.size_of)  # ❌ Expected '.' after '@intrinsic'
routine sizeof<T>() -> uaddr
```

**5. ~~Missing Labeled Argument Syntax:~~** ✅ IMPLEMENTED 2025-12-18
```razorforge
# Text.rf now parses correctly:
some_call(param: value)  # ✅ Works
```

**6. Doc Comments Not Supported:**
```razorforge
### This is a doc comment  # ❌ Unexpected token: DocComment
```

**7. Large Integer Literal Parsing:**
```razorforge
# s128.rf and u128.rf fail to parse:
preset S128_MIN: s128 = -170_141_183_460_469_231_731_687_303_715_884_105_728_s128  # ❌ Invalid
preset U128_MAX: u128 = 340282366920938463463374607431768211455_u128  # ❌ Invalid
```

**Tasks:**

**High Priority (Blocking):**
- [ ] Add `resident` keyword for permanent fixed-size reference types
- [ ] Add protocol abstract method parsing (routine declarations inside protocol)
- [ ] Fix large integer literal parsing (128-bit values)
- [ ] **Add const generic type constraints** (`where N is uaddr` syntax)
  - Parse `where N is <type>` constraints in generic declarations
  - Track which generic parameters are const vs type in constraint metadata
  - Use constraint info to disambiguate parsing at usage sites
  - Example: `resident FixedDict<K, V, N> where K follows Hashable where N is uaddr`
  - See updated Generics.md for full specification

**Medium Priority:**
- [ ] Add `where` keyword for generic constraints (basic protocol constraints)
- [ ] Add doc comment support (### syntax)
- [ ] Add `@intrinsic(...)` function body syntax

**Recently Completed (2025-12-19):**
- [x] ✅ **Stdlib: Type Conversion System Completed** - All 77 empty constructor bodies implemented
  - Comprehensive conversion matrix: sN ↔ uN ↔ fN ↔ dN (all combinations)
  - Widening conversions use proper LLVM intrinsics (sext, zext, fpext)
  - Narrowing conversions use truncation with overflow checks
  - Chaining through intermediate types for complex conversions
  - s64 checked arithmetic methods uncommented (tuple destructuring working)
  - Files: s16, u16, s64, s128, u128, f16, f32, f64, f128, d32, d64, d128
- [x] ✅ Add `follows` keyword for protocol implementation
- [x] ✅ Add `isnot`, `notfrom`, `notfollows` operators
- [x] ✅ Add `pass` statement support
- [x] ✅ Fix const generic parameter comparison parsing (me.slot < N now correctly parses as comparison, not generic call)
- [x] ✅ Support plain integer literals in const generic arguments (`ValueList<T, 10>`)
- [x] ✅ Fix lowercase generic function calls (`data_size<T>()` now works)
- [x] ✅ Add const generic type constraints (`where N is uaddr` syntax)
- [x] ✅ Register LlvmNativeI1 -> LLVM i1 mapping (fixes bool.rf code generation)
- [x] ✅ Add labeled argument syntax parsing (param: value in calls)
- [x] ✅ Fix nested generic parsing (`Tracked<Retained<T>>`, `List<List<T>>`)
- [x] ✅ Fix type parameter substitution in generic constructor calls (Snatched<T> within Snatched<T> methods now correctly substitutes T)

**Files Affected:**
- Keywords: ~50+ stdlib files
- Protocols: Crashable.rf, Comparable.rf, etc.
- Intrinsics: Runtime/compilerservice.rf
- Large integers: NativeDataTypes/s128.rf, u128.rf
- Doc comments: NativeDataTypes/Blank.rf

---

### Code Generation - Missing Primitive Type Registration

**Status:** ✅ **FIXED** (2025-12-19)

**Problem:** `bool.rf` declares its backing type as `LlvmNativeI1` but the code generator didn't recognize it.

**Solution:** Added `LlvmNativeI1` -> `i1` mapping in both MapTypeToLLVM and MapUnknownTypeToLLVM functions.

**Tasks:**
- [x] Register `LlvmNativeI1` -> LLVM `i1` mapping in code generator
- [x] Verify all other LLVM native types are registered correctly
- [ ] Add validation to catch missing type mappings earlier (future enhancement)

**Files Modified:** `src/CodeGen/LLVMCodeGenerator.cs:750,863`

---

### Semantic Analysis - Entry Point Namespace Restriction

**Status:** ⚠️ **DESIGN DECISION NEEDED**

```
Entry point 'start' must be in the global namespace (project root files),
not in namespace 'playground'
```

**Current Behavior:** Rejects `start()` in namespaced files.

**Question:** Should this be enforced? Or should entry point be allowed in any namespace?

**Options:**
1. Keep restriction - entry point must be in root files (no namespace)
2. Allow entry point in any namespace - use fully qualified name
3. Allow with warning - suggest moving to root

---

### Missing Standard Library Files

**Status:** 🔴 **BLOCKING**

```
Failed to import module 'Console': Module not found: core/Integral
```

**Problem:** Console.rf or its dependencies are missing/broken.

**Tasks:**
- [ ] Fix Console.rf import chain
- [ ] Ensure all core prelude files exist and parse correctly
- [ ] Add `show()` function (currently undefined)

---

## IMMEDIATE PRIORITIES

### 1. Generic Record Instantiation (BLOCKING STDLIB)

**Status:** 🟡 **IN PROGRESS** - Foundation laid, dependency ordering still needed

**Progress:**

- ✅ Created `GenericTypeResolver.cs` for generic type resolution infrastructure
- ✅ Created `CorePreludeLoader.cs` for standard library prelude management
- 🔴 Still need: dependency ordering, topological sort, recursive type handling

**Problem:** Struct definitions generated immediately without dependency ordering. Cannot handle recursive types or
complex instantiation patterns.

**Root Cause:**

- No dependency ordering (must generate `Node<T>` before `List<Node<T>>`)
- Circular dependencies not handled (`Node<T>` contains `Node<T>`)
- Fields may reference types that don't exist yet

**Concrete Tasks:**

1. Implement two-pass generation: collect all needed instantiations first
2. Build dependency graph and perform topological sort
3. Handle recursive types with forward declarations
4. Generate all struct definitions before any functions

**Files to Modify:**

- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - `InstantiateGenericRecord()` line ~245
- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - `InstantiateGenericEntity()` line ~290

**Estimated Effort:** 2-3 weeks

---

### 2. Native Runtime Library (BLOCKING EXECUTION)

**Status:** 🟡 **IN PROGRESS** - Text functions added, other functions still needed

**Progress:**

- ✅ Created `native/runtime/text_functions.c` with text formatting functions
- 🔴 Still need: stack trace runtime, console I/O, memory management

**Missing Functions:**

- ~~Text formatting: `format_s64`, `format_s32`, `format_f64`, etc.~~ ✅ DONE
- Stack trace runtime: `__rf_init_symbol_tables`, `__rf_stack_push`, `__rf_stack_pop`
- Console I/O: `rf_console_print_cstr`, `rf_console_get_line`, etc.
- Memory management: `rf_memory_alloc`, `rf_memory_free`, `rf_memory_copy`

**Concrete Tasks:**

1. Create `native/runtime.c` with implementations
2. Update build system to compile and link runtime.c
3. Test end-to-end: source → IR → executable → run

**Files:**

- `native/runtime.c` (create)
- `native/build.sh` (update)
- Build system integration

---

### 3. Type Conversion Method Syntax (BLOCKING IDIOMATIC CODE)

**Status:** 🔴 **BLOCKING** - Prevents idiomatic type conversion chains in stdlib

**Problem:** The compiler doesn't recognize that `value.Type()` should call `Type.__create__(from: ValueType)`. Type conversion chaining syntax fails at code generation.

**Current Behavior:**
```razorforge
# This should work but fails:
let x: d128 = ...
let y = x.s32().s8!()  # ❌ Failed to resolve type 'd128.s32' - method not found
```

**Expected Behavior:**
```razorforge
# Both syntaxes should work:
let y = s8!(s32(x))    # ✅ Constructor syntax works
let y = x.s32().s8!()  # ❌ Method chaining should work (doesn't)

# User guidance: "you can do type casting with T(u) or u.T()"
```

**Error Messages:**
```
Warning: Skipping function d128.to_s8 due to code generation error: [EG004]
  Failed to resolve type 'd128.s32' during code generation: method not found in loaded modules
Warning: Skipping function d128.to_u8 due to code generation error: [EG004]
  Failed to resolve type 'd128.u32!' during code generation: method not found in loaded modules
```

**Impact:**
- All decimal type conversions fail (d32, d64, d128)
- All floating-point type conversions fail (f32, f64)
- Cannot use idiomatic chaining syntax throughout stdlib
- Forces verbose nested constructor calls

**Root Cause:**
The code generator looks for actual method declarations like `routine d128.s32(me: d128) -> s32` but doesn't understand that `value.Type()` should desugar to `Type(value)` or find the appropriate `Type.__create__(from: ...)` constructor.

**Solution Approaches:**

**Option 1: Desugar in Semantic Analysis** (Recommended)
- When seeing `expr.TypeName()` where TypeName is a known type
- Transform to constructor call: `TypeName(expr)`
- Let existing constructor resolution find `TypeName.__create__(from: ExprType)`

**Option 2: Add Method Resolution Fallback**
- In code generator, when method `TypeName` not found on object
- Check if `TypeName` is a registered type
- Fall back to constructor call: `TypeName.__create__(from: object)`

**Option 3: Generate Synthetic Methods**
- For each `T.__create__(from: U)`, generate synthetic `routine U.T(me: U) -> T`
- Adds overhead but makes method truly exist

**Concrete Tasks:**

1. Add semantic analysis pass to detect type conversion method syntax
2. Transform `expr.TypeName()` to `TypeName(expr)` before type checking
3. Ensure crashable syntax `expr.TypeName!()` transforms to `TypeName!(expr)`
4. Add unit tests for type conversion chaining
5. Verify all stdlib type conversions compile correctly

**Files to Modify:**
- `src/Analysis/SemanticAnalyzer.cs` - Add type conversion desugaring pass
- `src/AST/Expressions.cs` - May need to track original vs desugared form
- Tests: Add type conversion chaining test cases

**Files Currently Affected:**
- `stdlib/NativeDataTypes/d32.rf` - All to_s8, to_u8, to_s16, to_u16, to_u32, to_u64
- `stdlib/NativeDataTypes/d64.rf` - All to_s8, to_u8, to_s16, to_u16, to_u32, to_u64
- `stdlib/NativeDataTypes/d128.rf` - All to_s8, to_u8, to_s16, to_u16, to_u32, to_u64
- `stdlib/NativeDataTypes/f32.rf` - to_s8, to_u8, to_s16, to_u16
- `stdlib/NativeDataTypes/f64.rf` - to_s8, to_u8, to_s16, to_u16

**Workaround:** Revert to explicit method call syntax `me.to_s32().to_s8!()` until fixed.

---

### 4. Generic Type Constructor Resolution (BLOCKING EXECUTION)

**Status:** 🔴 **BLOCKING** - Prevents compilation of generic types with self-referential constructors

**Problem:** When a generic type's method constructs an instance of itself using `GenericType<T>(...)`, the code generator fails to resolve the constructor for the instantiated type.

**Error:**
```
Compilation failed: [EG004] L:\programming\RiderProjects\RazorForge\stdlib\memory\wrapper\Snatched.rf:126:12:
  Failed to resolve type 'Snatched_letter8' during code generation:
  record type not found for constructor call
```

**Failing Code:**
```razorforge
# In Snatched.rf:126
routine Snatched<T>.offset(bytes: MemorySize) -> Snatched<T> {
    return Snatched<T>(address: me.address + bytes)  # ❌ Fails here
}
```

**Root Cause:**
When generating code for `Snatched<letter8>.offset()`, the constructor call `Snatched<T>(address: ...)` should:
1. Substitute T → letter8
2. Look up the mangled name `Snatched_letter8`
3. Find the record definition for constructor call

But currently the code generator looks for `Snatched_letter8` as a record type and fails to find it, even though:
- The type substitution in BuildFullTypeName was fixed (2025-12-19)
- The generic record should have been instantiated during analysis

**Possible Causes:**
1. Generic record instantiation not happening during semantic analysis
2. Instantiated records not registered in type registry before code generation
3. Constructor call resolution not checking instantiated generics
4. Mismatch between instantiation timing and usage timing

**Related Issues:**
- Type parameter substitution in BuildFullTypeName was fixed for type expressions
- But constructor call handling may need separate fix
- Similar to issue #1 (Generic Record Instantiation) but specific to constructors

**Concrete Tasks:**

1. Verify generic record instantiation happens during semantic analysis
2. Ensure instantiated records are registered before code generation starts
3. Add constructor resolution for instantiated generic types
4. Handle self-referential constructor calls within generic methods
5. Add test cases for generic types constructing themselves

**Files to Investigate:**
- `src/CodeGen/LLVMCodeGenerator.Constructors.cs:845` - HandleRecordConstructorCall
- `src/Analysis/SemanticAnalyzer.cs` - Generic instantiation during analysis
- `src/Analysis/GenericTypeResolver.cs` - Type instantiation registration
- `src/CodeGen/LLVMCodeGenerator.cs` - Type registry and lookup

**Files Currently Affected:**
- `stdlib/memory/wrapper/Snatched.rf:126` - offset() method
- Potentially all memory wrapper types with self-constructing methods
- Any generic type that constructs instances of itself

**Impact:** Critical - prevents compilation of core memory management types.

---

### 5. Method Mutation Inference (CRITICAL FOR TOKEN SYSTEM)

**Status:** 🔴 **CRITICAL** - Blocks token API design

**Why Critical:** Token system (`Viewed<T>`, `Hijacked<T>`, `Inspected<T>`, `Seized<T>`) requires knowing which methods
mutate `me`. Without this:

- API fragmentation (different methods on `Viewed<T>` vs `Hijacked<T>`)
- API duplication (`get()`/`get_mut()` pattern everywhere)
- Runtime failures (all methods compile but some panic)

**Concrete Tasks:**

1. Implement `MutationAnalyzer.cs` with direct mutation detection (field writes)
2. Add call graph construction
3. Add transitive propagation (if A calls mutating B, then A is mutating)
4. Integrate into semantic analysis pass
5. Add token type enforcement in code generation
6. Add return type wrapping for token propagation

**Files:**

- `src/Analysis/MutationAnalyzer.cs` (NEW FILE)
- `src/Analysis/SemanticAnalyzer.cs` - Integrate mutation analysis
- `src/Analysis/TypeInfo.cs` - Add mutation info storage
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Token enforcement
- `src/CodeGen/LLVMCodeGenerator.MethodCalls.cs` - Return type wrapping

**See Also:**
[RazorForge Memory Model](../wiki/RazorForge-Memory-Model.md), [RazorForge Routines](../wiki/RazorForge-Routines.md)

---

### 4. Migratable Modification Inference (CRITICAL FOR ITERATOR SAFETY)

**Status:** 🔴 **CRITICAL** - Blocks safe iteration and container modification

**Why Critical:** Iterator invalidation is a major source of bugs in systems languages. The migratable inference system
prevents buffer relocation during iteration, ensuring iterator safety without manual tracking.

**Problem:** No detection of which methods can relocate container buffers (like `DynamicSlice`). Without this:

- Iterator invalidation bugs possible
- Unsafe to modify containers during iteration
- No compile-time protection against buffer relocation

**The Rule:**

- **Migratable operations** = operations that can relocate container buffers (change `DynamicSlice` address/capacity)
- **Banned during iteration** for ALL tokens (Viewed, Hijacked, Inspected, Seized)
- **Allowed outside iteration** for exclusive tokens (Hijacked/Seized) and owned containers

**Base Case: DynamicSlice Buffer Relocation**

A method is **migratable** if it can relocate the `DynamicSlice` buffer:

```razorforge
entity List<T> {
    private var buffer: DynamicSlice  # Heap allocation control structure
    private var count: uaddr
}

# MIGRATABLE - can relocate buffer
routine List<T>.push(value: T) {
    # May resize buffer, trigger reallocation → migratable
}

# MIGRATABLE - can relocate buffer
routine List<T>.pop!() -> T {
    # May shrink buffer, trigger reallocation → migratable
}

# NON-MIGRATABLE - only reads existing allocation
routine List<T>.__getitem__(index: uaddr) -> T {
    danger! { return me.buffer.read_as<T>(offset: index) }
}

# NON-MIGRATABLE - only writes to existing allocation
routine List<T>.__setitem__(index: uaddr, value: T) {
    danger! { me.buffer.write_as<T>(offset: index, value: value) }
}
```

**Enforcement Examples:**

```razorforge
# ✅ ALLOWED - Exclusive token, outside iteration
hijacking list as h {
    h.push(new_item)   # ✅ OK - not iterating
    h.pop()            # ✅ OK - not iterating
}

# ❌ BANNED - Migratable during iteration
hijacking list as h {
    for item in h {
        item.mutate()     # ✅ OK - element mutation
        h[0] = new_value  # ✅ OK - write in-place
        h.push(x)         # ❌ IllegalMigrationError CE - migratable during iteration!
    }
}

# ❌ BANNED - Read-only tokens never allow migratable
viewing list as v {
    v.push(x)  # ❌ Compile error - read-only token
}
```

**Implementation Algorithm:**

Similar to mutation inference (three-phase analysis):

**Phase 1: Direct Analysis**

```
For each method:
    If modifies DynamicSlice control structure (pointer/size/capacity):
        Check if modification can trigger buffer relocation
        If yes → mark as migratable
```

**Phase 2: Call Graph Propagation**

```
Build call graph
For each method that calls a migratable method on me:
    Mark as migratable
Repeat until fixpoint (no changes)
```

**Phase 3: Enforcement**

```
At each method call:
    If currently iterating:
        If method is migratable → IllegalMigrationError CE
    If token is Viewed<T> or Inspected<T>:
        If method is migratable → compile error (read-only)
```

**Key Detection Rules:**

1. **Direct field writes to DynamicSlice** that can cause reallocation:
    - `buffer.resize!(new_size)` → migratable
    - `buffer = new DynamicSlice(...)` → migratable
    - `buffer.push(...)` → migratable (if buffer is DynamicSlice-based)

2. **Calls to migratable methods** → caller becomes migratable (transitive)

3. **Conditional branches** - If ANY path can trigger migration → entire method is migratable

**Concrete Tasks:**

1. Create `MigratableAnalyzer.cs` with DynamicSlice modification detection
2. Add call graph analysis for transitive propagation
3. Track iteration state in semantic analyzer
4. Add compile-time enforcement: check if method is migratable during iteration
5. Generate `IllegalMigrationError` compile error with helpful message
6. Integrate with token system (read-only tokens always ban migratable)

**Files to Create/Modify:**

- `src/Analysis/MigratableAnalyzer.cs` (NEW FILE) - Core migratable inference logic
- `src/Analysis/SemanticAnalyzer.cs` - Integrate migratable analysis, track iteration state
- `src/Analysis/TypeInfo.cs` - Add `IsMigratable: bool` field to method metadata
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Enforce migratable restrictions
- `src/AST/Statements.cs` - Track iteration state in for-loop AST

**Error Messages:**

```
Iterator Invalidation Error: Using potentially migrating operation 'List<T>.push' while iterating is illegal for stability reasons.

  12 | for item in list {
  13 |     list.push(item)  # ❌ Buffer relocation during iteration
     |          ^^^^ potentially migrating operation called here
  14 | }

Note: This operation can relocate the container buffer, invalidating the iterator.
Hint: Collect items first, then modify the container after iteration completes.
```

**Error Class:** `IteratorInvalidationError` (RazorForge) / `ModifyWhileIteratingError` (Suflae)

**Estimated Effort:** 2-3 weeks

**Method Permission Levels (Inferred Automatically):**

The compiler automatically infers three permission levels for methods:

| Level          | Property             | Meaning                       | Example                                 |
|----------------|----------------------|-------------------------------|-----------------------------------------|
| **Readonly**   | No mutation          | Only reads fields             | `List<T>.count()`, `List<T>.get(index)` |
| **Writable**   | Mutates in-place     | Writes to existing allocation | `List<T>.__setitem__(index, value)`     |
| **Migratable** | Can relocate buffers | May trigger reallocation      | `List<T>.push(value)`, `List<T>.pop()`  |

**Important:** All three levels are **automatically inferred** - no manual annotations needed. The compiler determines:

- **Readonly** via mutation inference (no field writes)
- **Writable** via mutation inference (field writes but no buffer relocation)
- **Migratable** via migratable inference (can relocate DynamicSlice buffers)

**Language-Specific Error Messages:**

| Aspect              | RazorForge (systems)                                                                | Suflae (beginners)                                                              |
|---------------------|-------------------------------------------------------------------------------------|---------------------------------------------------------------------------------|
| **Error class**     | `IteratorInvalidationError`                                                         | `ModifyWhileIteratingError`                                                     |
| **Error display**   | "Iterator Invalidation Error: ..."                                                  | "Modify While Iterating Error: ..."                                             |
| **Technical terms** | "potentially migrating operation"<br>"buffer relocation"<br>"iterator invalidation" | "modify list while looping"<br>"add/remove items"<br>"skip/process twice/crash" |
| **Explanation**     | Low-level buffer mechanics                                                          | High-level concrete consequences                                                |
| **Audience**        | Systems programmers who understand memory                                           | Beginners learning programming                                                  |

**Example Comparison:**

```razorforge
# RazorForge error:
Iterator Invalidation Error: Using potentially migrating operation 'List<T>.push' while iterating is illegal for stability reasons.
Note: This operation can relocate the container buffer, invalidating the iterator.
Hint: Collect items first, then modify the container after iteration completes.

# Suflae error:
Modify While Iterating Error: Cannot modify list while looping through it
Why: Adding or removing items can skip some items, process items twice, or crash the program.
How to fix: Collect items first, then modify the list after the loop finishes.
```

Same safety guarantee, different messaging approach.

**See Also:
** [RazorForge Compiler Analysis](../wiki/RazorForge-Compiler-Analysis.md#migratable-modification-inference) - Section
on migratable inference

---

### 5. Crashable Function Tracking & Variant Generation (CRITICAL FOR ERROR HANDLING)

**Status:** 🔴 **NOT IMPLEMENTED** - Blocks error handling system

**Problem:** No tracking of which functions can crash (use `!` suffix). No auto-generation of error-handling variants.

**Note:** The crash intrinsic is `stop!()`, not `crash!()`.

**The Four Function Types:**

| Type             | Naming        | Who Writes | Behavior                                 | Example                                 |
|------------------|---------------|------------|------------------------------------------|-----------------------------------------|
| **1. Crashable** | `foo!()`      | User       | Crashes on error/absent                  | `read_file!(path) -> Text`              |
| **2. Try**       | `try_foo()`   | Compiler   | Returns `T?` (None on error/absent)      | `try_read_file(path) -> Text?`          |
| **3. Check**     | `check_foo()` | Compiler   | Returns `Result<T>` (error info)         | `check_read_file(path) -> Result<Text>` |
| **4. Find**      | `find_foo()`  | Compiler   | Returns `Lookup<T>` (error/absent/value) | `find_get_user(id) -> Lookup<User>`     |

**Auto-Generation Rules:**

The compiler generates variants based on what keywords appear in the `!` function body:

| `throw` in body? | `absent` in body? | Compiler generates                                              |
|------------------|-------------------|-----------------------------------------------------------------|
| ❌ No             | ❌ No              | **Compile Error** - `!` functions must have `throw` or `absent` |
| ❌ No             | ✅ Yes             | `try_` only                                                     |
| ✅ Yes            | ❌ No              | `try_`, `check_`                                                |
| ✅ Yes            | ✅ Yes             | `try_`, `find_`                                                 |

**Examples for Each Rule:**

```razorforge
# Rule 1: No throw, no absent → Compile Error
routine calculate!(a: s32, b: s32) -> s32 {
    return a + b  # ❌ ERROR: No throw or absent in ! function
}

# Rule 2: absent only (no throw) → Generates try_ only
routine get_item!(id: u64) -> Item {
    unless storage.has(id) {
        absent  # Only absent, no throw
    }
    return storage.get(id)
}
# Compiler generates: try_get_item() -> Item?
# User wrote: get_item!() -> Item (crashes with AbsentValueError)

# Rule 3: throw only (no absent) → Generates try_, check_
routine process_data!(input: Text<letter32>) -> Data {
    if input.is_empty() {
        throw ValidationError(message: "Empty input")
    }
    return Data(input)
}
# Compiler generates:
#   try_process_data() -> Data?
#   check_process_data() -> Result<Data>
# User wrote: process_data!() -> Data (crashes on throw)

# Rule 4: BOTH throw AND absent → Generates try_, find_
routine get_user!(id: u64) -> User {
    if id == 0 {
        throw ValidationError(message: "Invalid ID")
    }
    unless database.has(id) {
        absent  # Has both throw and absent → generates find_
    }
    return database.get(id)
}
# Compiler generates:
#   try_get_user() -> User?
#   find_get_user() -> Lookup<User>
# User wrote: get_user!() -> User (crashes on throw or absent)
```

**Critical Insight - Body Transformation:**

When generating non-crashable variants, statements are transformed as follows:

- `check_` variant (returns `Result<T>`): `throw E` → `return E`, `return V` → `return V`
- `try_` variant (returns `T?`): `throw E` → `return None`, `absent` → `return None`, `return V` → `return V`
- `find_` variant (returns `Lookup<T>`): `throw E` → `return E`, `absent` → `return None`, `return V` → `return V`

**Current Issues:**

- ❌ No detection of `!` function calls
- ❌ No enforcement that non-`!` functions can't call `!` functions directly
- ❌ No auto-generation of `try_`, `check_`, and `find_` variants
- ❌ No body transformation (`throw`/`absent` → return conversion)
- ❌ No detection of `throw` and `absent` keywords in function bodies
- ❌ No validation of `@crash_only` attribute
- ❌ No tracking of which variant is being called

**Concrete Tasks:**

1. **Detect keywords in `!` function bodies:**
    - Walk AST to find `throw` statements
    - Walk AST to find `absent` statements
    - Validate: `!` functions MUST have at least one `throw` or `absent`
    - Store keyword presence in function metadata

2. **Track function error category in AST/symbol table:**
    - Detect `!` suffix on function name (user-written crashable)
    - Store generated variant types: `try_`, `check_`, `find_`
    - Store in `FunctionInfo` with enum: `Crashable | TryVariant | CheckVariant | FindVariant`

3. **Call site validation:**
    - Detect calls to `!` functions
    - Enforce: only callable from other `!` functions or with explicit `try_`/`check_`/`find_` prefix
    - Generate error: "Cannot call crashable function 'foo!' from non-crashable context. Use try_foo(), check_foo(), or
      find_foo()"

4. **Auto-generate variants with body transformation:**
    - From `foo!(x: T) -> U`, generate 1-2 variants based on body content:
        - `try_foo(x: T) -> U?` - ALWAYS generated
        - `check_foo(x: T) -> Result<U>` - if body has `throw` but NOT `absent`
        - `find_foo(x: T) -> Lookup<U>` - if body has BOTH `throw` AND `absent`
    - Store all versions in symbol table
    - Mark generated functions as compiler-generated (cannot be manually written)

5. **Body transformation algorithm:**
   ```
   For try_ variant (returns T?):
   - Walk AST of ! function body
   - Replace: throw <expr> → return None
   - Replace: absent → return None
   - Replace: return <expr> → return <expr>
   - Replace calls to other ! functions: foo!(x) → try_foo(x)?

   For check_ variant (returns Result<T>):
   - Walk AST of ! function body
   - Replace: throw <expr> → return <expr>
   - Replace: return <expr> → return <expr>
   - Replace calls to other ! functions: foo!(x) → check_foo(x)?

   For find_ variant (returns Lookup<T>):
   - Walk AST of ! function body
   - Replace: throw <expr> → return <expr>
   - Replace: absent → return None
   - Replace: return <expr> → return <expr>
   - Replace calls to other ! functions: foo!(x) → find_foo(x)?
   ```

6. **Validate `@crash_only` attribute:**
    - Only allowed on `start!` function
    - Required if `start` has `!` suffix
    - Enables crashable calls in entry point without transformation

7. **Transitive analysis:**
    - If function A calls crashable function B without try_/check_/find_ prefix, then A must be crashable
    - Build call graph and propagate crashability

**Example Enforcement:**

```razorforge
# User writes crashable function with throw only
routine parse_config!(path: Text<letter32>) -> Config {
    let data = read_file!(path)  # ✅ OK - parse_config! is crashable
    unless data.contains("version") {
        throw ConfigError(message: "Missing version field")
    }
    return Config.deserialize!(data)  # ✅ OK
}

# Non-crashable function cannot call ! directly
routine safe_parse(path: Text<letter32>) -> Config? {
    let data = read_file!(path)  # ❌ ERROR: Cannot call crashable function
    # Fix: use try_ variant
    let data = try_read_file(path)?  # ✅ OK - returns Text? (None on error)
    return try_Config_deserialize(data)?  # ✅ OK
}

# Compiler auto-generates try_ and check_ (because body has throw but no absent):
routine try_parse_config(path: Text<letter32>) -> Config? {
    let data = try_read_file(path)?  # Transformed: read_file! → try_read_file
    unless data.contains("version") {
        return None  # Transformed: throw → return None
    }
    return try_Config_deserialize(data)?  # Transformed
}

routine check_parse_config(path: Text<letter32>) -> Result<Config> {
    let data = check_read_file(path)?  # Transformed: read_file! → check_read_file
    unless data.contains("version") {
        return ConfigError(message: "Missing version field")  # Transformed: throw E → return E
    }
    return check_Config_deserialize(data)?  # Transformed
}

# Example with both throw AND absent → generates try_ and find_
routine get_user!(id: u64) -> User {
    if id == 0 {
        throw ValidationError(message: "Invalid ID")
    }
    unless database.has(id) {
        absent  # Has both throw and absent
    }
    return database.get(id)
}

# Compiler auto-generates:
routine try_get_user(id: u64) -> User? {
    if id == 0 {
        return None  # throw → None
    }
    unless database.has(id) {
        return None  # absent → None
    }
    return database.get(id)
}

routine find_get_user(id: u64) -> Lookup<User> {
    if id == 0 {
        return ValidationError(message: "Invalid ID")  # throw E → return E
    }
    unless database.has(id) {
        return None  # absent → None
    }
    return database.get(id)
}
```

**Files:**

- `src/Analysis/CrashableAnalyzer.cs` (NEW FILE - detect `throw`/`absent` in bodies, validation)
- `src/Analysis/FunctionVariantGenerator.cs` (NEW FILE - auto-gen try_/check_/find_ with body transform)
- `src/AST/ASTTransformer.cs` (NEW FILE - throw/absent → return conversion)
- `src/Analysis/SemanticAnalyzer.cs` - Integrate crashable validation
- `src/Analysis/SymbolTable.cs` - Add function variant enum: Crashable | TryVariant | CheckVariant | FindVariant
- `src/CodeGen/LLVMCodeGenerator.Functions.cs` - Generate all variants (1 user + 1-2 generated)
- `src/Parser/RazorForgeParser.cs` - Parse `@crash_only` attribute, `throw` and `absent` statements

**LSP Integration:**
The LSP should track and visualize all four function types:

- Color-code by error handling: `!` functions in red, `check_` in yellow, `try_` in blue, `find_` in purple
- Show in hover: "Crashable function", "Auto-generated try_ variant from foo!", etc.
- Diagnostic: Highlight crashable calls in non-crashable contexts
- Quick fix: "Replace with try_foo()", "Replace with check_foo()", or "Replace with find_foo()"

**See Also:** [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md)

---

### 5. Result<T>/Lookup<T> Opaque Types & Type Narrowing (CRITICAL FOR ERROR HANDLING)

**Status:** 🔴 **NOT IMPLEMENTED** - Blocks error handling ergonomics

**Problem:** Result<T> and Lookup<T> need special compiler treatment as opaque types with NO API, only accessible via
language constructs.

**Design Decision:**

- Result<T> and Lookup<T> are **compiler-provided opaque types** (NOT normal variants)
- Variant is a **general-purpose sum type** (user-defined, more permissive)

**Result<T>/Lookup<T> Restrictions ("Use Immediately" Semantics):**

- ✅ Can store in local variables
- ✅ Can dismantle with `if is` (with type narrowing/auto-unwrap)
- ✅ Can dismantle with `when` (exhaustive pattern matching)
- ✅ Can unwrap with `??` (provide default)
- ❌ **NO methods** - no API surface at all (no `.or()`, `.select()`, `.is_valid()`, etc.)
- ❌ Cannot pass to user functions as parameters (except functions that immediately dismantle)
- ❌ Cannot return from user functions (only compiler-generated `check_`/`find_` can return them)
- ❌ Cannot store in collections
- ❌ Cannot store in record/entity fields

**Variant Permissions (General-Purpose Sum Type):**

- ✅ Can store in local variables
- ✅ Can pass to functions as parameters
- ✅ Can return from functions
- ✅ Can store in collections
- ✅ Can store in record/entity fields (where appropriate)
- ✅ Can pattern match with `when`
- ✅ Can pattern match with `if is`
- ❌ **NO methods** - no API surface (no `.or()`, `.select()`, etc.)
- ❌ Cannot unwrap with `??` (only Result/Lookup support this)
- ⚠️ Must pattern match to access payload

**The Three Dismantling Constructs:**

1. **`??` operator** (simple default):

    ```razorforge
    let result: Result<s32> = check_s32("42")
    let num: s32 = result ?? 0  # Unwraps or provides default
    
    let lookup: Lookup<User> = find_user(42)
    let user: User = lookup ?? User.guest()  # Ignores BOTH error and absent
    ```

2. **`when` pattern matching** (exhaustive handling):

    ```razorforge
    # Result<T> - two-way
    when result {
        is Crashable e => handle_error(e)
        else value => use(value)  # Auto-unwrapped
    }
    
    # Lookup<T> - three-way
    when lookup {
        is Crashable e => handle_error(e)
        is None => handle_absent()
        else value => use(value)  # Auto-unwrapped
    }
    
    # Variant - must specify all cases or use else
    when variant {
        is Case1(payload) => handle1(payload)
        is Case2(payload) => handle2(payload)
        else other => handle_other(other)
    }
    ```

3. **`if is` type narrowing** (NEW - early return pattern):

    ```razorforge
    # Result<T> - early return
    routine process_input(input: Text) {
        let result = check_s32(input)
        if result is Crashable e {
            show(f"Error: {e.crash_message()}")
            return
        }
        # result is auto-unwrapped to s32 here
        show(f"Success: {result}")
    }
    
    # Lookup<T> - sequential narrowing (three-way)
    routine process_user(id: u64) {
        let lookup = find_user(id)
    
        if lookup is Crashable e {
            show(f"Database error: {e.crash_message()}")
            return
        }
        # lookup narrowed to: None | User
    
        if lookup is None {
            show("User not found")
            return
        }
        # lookup is auto-unwrapped to User here
        show(f"Found: {lookup.name}")
    }
    
    # Variant - early return for specific case
    routine handle_response(response: HttpResponse) {
        if response is ERROR(err) {
            log_error(err)
            return
        }
        # response is narrowed (compiler knows it's not ERROR)
        # Continue processing other cases...
    }
    ```

**Concrete Tasks:**

1. **Implement opaque type system:**
    - Mark Result<T> and Lookup<T> as compiler-provided opaque types
    - Generate internal representation (e.g., discriminated union with Snatched<Crashable>)
    - Hide internal structure from users (no field access)
    - Store metadata: "this is Result<T>, not a normal variant"

2. **Implement `if is` type narrowing:**
    - Parse `if <expr> is <pattern>` syntax
    - Support type patterns: `is Crashable e`, `is None`, `is VariantCase (payload)`
    - Perform type narrowing in subsequent code
    - Track narrowed type in symbol table for that scope
    - Generate correct LLVM IR for narrowed type access
    - Handle multiple sequential `if is` (e.g., Lookup<T> three-way narrowing)

3. **Enforce "use immediately" restrictions:**
    - Reject function parameter types: `routine foo(r: Result<T>)` → CE (except compiler-generated)
    - Reject function return types: `routine foo() -> Result<T>` → CE (except compiler-generated)
    - Reject collection types: `List<Result<T>>` → CE
    - Reject field types: `entity Foo { result: Result<T> }` → CE
    - Allow local variables: `let r: Result<T> = check_foo()` ✅
    - Allow compiler-generated functions to return Result/Lookup

4. **Allow Variant to be passed/returned:**
    - Accept function parameters: `routine foo(v: Variant)` ✅
    - Accept function returns: `routine foo() -> Variant` ✅
    - Accept collection types: `List<Variant>` ✅
    - Accept field types: `entity Foo { variant: Variant }` ✅
    - Still require pattern matching to access payload

5. **Implement type narrowing tracking:**
    - Track original type before narrowing
    - Track narrowed type after `if is` check
    - Invalidate narrowing on reassignment
    - Handle control flow (early returns, branches)
    - Generate appropriate LLVM IR for auto-unwrapped access

6. **Validation:**
    - Error message: "Result<T> can only be stored in local variables - cannot pass to functions"
    - Error message: "Lookup<T> can only be stored in local variables - cannot return from user functions"
    - Error message: "Result<T>/Lookup<T> have no methods - use ??, when, or if is"
    - Error message: "Variant has no methods - use pattern matching with when or if is"

**Example Enforcement:**

```razorforge
# ❌ CE: Cannot pass Result to user function
routine log_and_handle(r: Result<Config>) {  # ERROR!
    # ...
}

# ❌ CE: Cannot return Result from user function
routine my_check() -> Result<s32> {  # ERROR!
    # Only compiler-generated check_ can return Result
}

# ❌ CE: Cannot store in collection
let results = List<Result<s32>>()  # ERROR!

# ✅ OK: Can store in local, dismantle immediately
let result = check_s32("42")
if result is Crashable e {
    show("Error")
    return
}
show(f"Value: {result}")  # result is s32 here

# ✅ OK: Variant can be passed/returned
routine parse_json(text: Text) -> JsonValue {
    # JsonValue is a variant
    return JsonValue.Object(...)  # OK!
}

routine process_json(json: JsonValue) {  # OK - can accept variant
    when json {
        is String(s) => show(s)
        is Number(n) => show(n.to_text())
        else => show("Other")
    }
}
```

**Files:**

- `src/Analysis/OpaqueTypeValidator.cs` (NEW FILE - enforce use-immediately semantics)
- `src/Analysis/TypeNarrowingAnalyzer.cs` (NEW FILE - track if is narrowing)
- `src/Parser/RazorForgeParser.Expressions.cs` - Parse `if is` syntax
- `src/AST/Statements.cs` - Add IfIsStatement node
- `src/Analysis/SemanticAnalyzer.cs` - Integrate type narrowing
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Generate narrowed type access
- `src/Analysis/TypeInfo.cs` - Add opaque type markers, narrowed type tracking

**See Also:
** [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md), [RazorForge Variants](../wiki/RazorForge-Variants.md)

---

### 6. Compiler-Generated Methods (DEFAULT + OVERRIDABLE)

**Status:** 🟡 **DESIGN COMPLETE** - Documented but not implemented

**Problem:** All types (records, entities, residents, choices) need default implementations for standard methods that
can be overridden by users.

**Compiler-Generated Methods:**

All types (except variants) automatically get these methods:

| Method                                                                  | Records | Entities | Residents | Choices | Notes                                                         |
|-------------------------------------------------------------------------|---------|----------|-----------|---------|---------------------------------------------------------------|
| `to_text() -> Text`                                                     | ✓       | ✓        | ✓         | ✓       | User-facing display                                           |
| `to_debug() -> Text`                                                    | ✓       | ✓        | ✓         | N/A     | Debug display (entities show all fields including private)    |
| `memory_size() -> MemorySize`                                           | ✓       | ✓        | ✓         | N/A     | Fixed size for records/residents, shallow for entities        |
| `hash() -> s64` (RF) / `-> Integer` (SF)                                | ✓       | ❌*       | ❌         | ✓       | *Entities not hashable except `Text<T>` in RazorForge         |
| `id() -> u64` (RF) / `-> Integer` (SF)                                  | N/A     | ✓        | ✓         | N/A     | Unique identity (memory address-based)                        |
| `copy!() -> T` (RF) / `copy() -> T` (SF)                                | N/A     | ✓        | N/A       | N/A     | Deep copy                                                     |
| `__eq__(other: Me) -> bool`                                             | ✓       | ✓        | ✓         | ✓       | Value equality (operator `==`)                                |
| `__ne__(other: Me) -> bool`                                             | ✓       | ✓        | ✓         | ✓       | Negation (operator `!=`) - compiler generated, never override |
| `to_integer() -> s32` (RF) / `-> Integer` (SF)                          | N/A     | N/A      | N/A       | ✓       | Zero-based enum value                                         |
| `__create__(from: Text) -> T` (RF) / `from_text(text: Text) -> T?` (SF) | N/A     | N/A      | N/A       | ✓       | Parse string to choice                                        |
| `all_cases() -> List<T>`                                                | N/A     | N/A      | N/A       | ✓       | All possible values                                           |

**Special Compiler Operators (cannot be overridden):**

- `===`, `!==` - Reference equality/inequality (entities and residents only)
- `is`, `isnot` - Type checking (all types)

**Default Behavior:**

1. **Records:**
    - `to_text()`: Constructor syntax with all fields `Point(x: 1.0, y: 2.0)`
    - `to_debug()`: Same as `to_text()` (all fields always visible)
    - `memory_size()`: `sizeof(RecordType)` - fixed at compile time
    - `hash()`: Hash of all fields combined
    - `__eq__()`: Field-by-field value comparison

2. **Entities:**
    - `to_text()`: Type name with public/internal/family fields only
    - `to_debug()`: ALL fields including private, with ID
    - `memory_size()`: Shallow size (struct only, not heap data)
    - `hash()`: **NOT AVAILABLE** (except `Text<T>` which is hashable)
    - `id()`: Unique object identity (never override)
    - `copy()`: Deep copy of all fields
    - `__eq__()`: Field-by-field value comparison

3. **Residents:**
    - `to_text()`: Type name with all fields
    - `to_debug()`: All fields with ID and memory location
    - `memory_size()`: `sizeof(ResidentType)` - fixed at compile time
    - `hash()`: **NOT AVAILABLE**
    - `id()`: Stable memory address (never override)
    - `__eq__()`: Field-by-field value comparison

4. **Choices:**
    - `to_text()`: Case name as string (e.g., `"READ"`)
    - `hash()`: Based on enum value
    - `to_integer()`: Zero-based ordinal (e.g., `READ -> 0`, `WRITE -> 1`)
    - `__eq__()`: Enum value comparison
    - `__create__()` / `from_text()`: Parse string to case (crash/return None if invalid)
    - `all_cases()`: List of all possible values

5. **Variants:**
    - ❌ **NO METHODS AT ALL** - only pattern matching operators
    - ✅ `is`, `isnot` - Type checking and pattern matching (built-in operators)
    - Pattern matching with `when` and `if is`
    - Variants are "random boxes" for immediate unpacking, not data objects
    - The payload has the methods you need, not the variant wrapper

**Method Signature Conventions:**

- Instance methods: `TypeName.method_name() -> ReturnType` (no explicit `me` in signature)
- The `me` parameter is implicit and available in method body
- Operator overloads: `TypeName.__eq__(other: Me) -> bool`

**Override Examples:**

```razorforge
# Override to_text() for custom format
record Temperature {
    celsius: f32
}

routine Temperature.to_text() -> Text<letter32> {
    let fahrenheit = me.celsius * 9.0 / 5.0 + 32.0
    return f"{me.celsius}°C ({fahrenheit}°F)"
}

# Override __eq__ for custom equality (e.g., approximate float comparison)
record FloatPair {
    a: f32
    b: f32
}

routine FloatPair.__eq__(other: FloatPair) -> bool {
    let epsilon = 0.0001
    return abs(me.a - other.a) < epsilon and abs(me.b - other.b) < epsilon
}

# Override memory_size() for deep accounting
entity DocumentTree {
    var root: Document
    var children: List<Document>
}

routine DocumentTree.memory_size() -> MemorySize {
    var total = 16b  # Struct overhead
    total += me.root.memory_size()
    for child in me.children {
        total += child.memory_size()
    }
    return total
}
```

**Concrete Tasks:**

1. **Implement method generation infrastructure:**
    - Add `CompilerGeneratedMethodGenerator.cs`
    - Generate default implementations during semantic analysis
    - Store in symbol table as overridable methods

2. **Type-specific generation:**
    - Record methods (to_text, to_debug, memory_size, hash, `__eq__`, `__ne__`)
    - Entity methods (to_text, to_debug, memory_size, id, copy, `__eq__`, `__ne__`)
    - Resident methods (to_text, to_debug, memory_size, id, `__eq__`, `__ne__`)
    - Choice methods (to_text, to_integer, hash, `__eq__`, `__eq__`, `__create__`, all_cases)

3. **Override detection:**
    - Check if user-defined method matches compiler-generated signature
    - Replace default implementation with user version
    - Validate override correctness (matching signature)

4. **Special operator handling:**
    - `===`, `!==` - Built-in reference equality (cannot override)
    - `is`, `isnot` - Built-in type checking (cannot override)
    - Block attempts to override these operators

5. **Code generation:**
    - Generate LLVM IR for default implementations
    - Link to user-provided overrides when present
    - Ensure correct method dispatch

**Files:**

- `src/Analysis/CompilerGeneratedMethodGenerator.cs` (NEW FILE)
- `src/Analysis/SemanticAnalyzer.cs` - Integrate method generation
- `src/Analysis/SymbolTable.cs` - Store overridable methods
- `src/CodeGen/LLVMCodeGenerator.Methods.cs` - Generate method implementations
- `src/Parser/RazorForgeParser.cs` - Parse method overrides

**See Also:
** [RazorForge Records](../wiki/RazorForge-Records.md), [RazorForge Entities](../wiki/RazorForge-Entities.md), [RazorForge Residents](../wiki/RazorForge-Residents.md), [RazorForge Choices](../wiki/RazorForge-Choices.md), [Suflae Records](../wiki/Suflae-Records.md), [Suflae Entities](../wiki/Suflae-Entities.md), [Suflae Choices](../wiki/Suflae-Choices.md)

---

## CORE FEATURES STATUS

### Generics

**Status:** ✅ **CORE FUNCTIONALITY COMPLETE** (2025-12-04)

**What Works:**

- ✅ Generic function monomorphization (`identity<T>`, `swap<T>`)
- ✅ Namespace-qualified generic methods (`Console.show<T>`)
- ✅ Nested generic types (`List<List<s32>>`, `Range<BackIndex<uaddr>>`)
- ✅ Generic type instantiation (`TestType<s64>(...)`)
- ✅ Generic method calls (`instance.method()`)
- ✅ Template matching (concrete types match templates automatically)
- ✅ End-to-end compilation with full type checking

**Remaining Work:**

1. **Generic Record Instantiation** (See Immediate Priorities #1)
2. **Generic Function Overload Resolution** - Cannot choose between generic and non-generic versions
3. **Generic Constraints** - Cannot express type requirements (`<T: Comparable>`)

**See:** [GENERICS-STATUS.md](GENERICS-STATUS.md), [BUG-12.13-ANALYSIS.md](BUG-12.13-ANALYSIS.md)

---

### Type System

**Status:** ⏳ **PARTIALLY IMPLEMENTED**

**Working:**

- ✅ Record wrapper architecture ("everything is a record")
- ✅ Extract-operate-wrap pattern consistently applied
- ✅ Multi-field struct handling
- ✅ Nested record types (e.g., `%d32 = type { %u32 }` where `%u32 = type { i32 }`)
- ✅ Type conversion and casting with auto-cast for variable initialization
- ✅ Floating-point type detection and operations

**Missing:**

- ❌ Optional field syntax (`field: Type?`) - Parser rejects `?` in field declarations (Bug 12.17)
- ❌ Type constructor calls - `saddr(me)` generates function call instead of type conversion (Bug 12.7)
- ❌ Integer literal type inference - Literals don't respect expected type context (Bug 12.10)
- ❌ Function return type tracking - Defaults to i32 instead of actual return type (Bug 12.11)

---

### Module System & Core Prelude

**Status:** ✅ **CORE PRELUDE WORKING** | ⏳ **MODULE IMPORTS BROKEN**

**Working:**

- ✅ Core prelude auto-loading (`namespace Core` files loaded automatically)
- ✅ Primitive types always available (s8-s128, u8-u128, f16-f128, d32-d128, bool)
- ✅ Error types always available (Maybe, Result, Lookup, DataHandle, DataState)
- ✅ Memory types always available (DynamicSlice, MemorySize, BackIndex, Range)
- ✅ Transitive dependency loading for core modules

**Broken:**

- ❌ Import statements fail to resolve modules even with `--stdlib` flag (Bug 12.15)
- ❌ Module resolver not finding stdlib files
- ❌ Selective imports not working (`import Collections/{List, Dict}`)

**Design Decisions (Finalized):**

1. **Search Order:** `stdlib/` → project root → external packages
2. **Import Style:** Unqualified access - `import Collections/List` → use `List<T>` directly
3. **Core Prelude:** `core` namespace automatically loaded (no import needed)
4. **Loading Strategy:** Eager - load all transitive imports before semantic analysis

**Implementation Tasks:**

- [ ] Fix module path resolution (Bug 12.15)
- [ ] Implement selective imports
- [ ] Add circular import detection
- [ ] Add collision detection for duplicate names

**Files:**

- `src/Analysis/ModuleResolver.cs` - Fix path resolution
- `src/Analysis/CorePreludeLoader.cs` - ✅ Complete
- `src/Analysis/SemanticAnalyzer.cs` - Fix import processing

**See Also:** [Modules-and-Imports.md](../wiki/Modules-and-Imports.md)

---

### Protocol System

**Status:** ⏳ **INFRASTRUCTURE EXISTS, IMPLEMENTATION INCOMPLETE**

**Current State:**

- `WellKnownProtocols` class defines standard protocols
- `TypeInfo.Protocols` tracks protocol membership
- Primitive types have correct protocols assigned

**Missing:**

- ❌ Protocol declarations (parser support for `protocol` keyword)
- ❌ Default implementations
- ❌ Mutation annotations (`@readonly`, `@writable`, `@migratable`)
- ❌ Implementation checking and verification
- ❌ Generic constraints (`<T follows Protocol>`)

**Key Design Points:**

- Protocols define method contracts ONLY (no fields)
- Mutation annotations are part of contract
- Default implementations declared OUTSIDE protocol block (saves indentation)
- `Me` (capitalized) as type placeholder, `me` (lowercase) as instance reference
- Types implement protocols via extension methods

**Standard Protocols:**

- **Comparable:** ONE method - `@readonly routine Me.__cmp__(other: Me) -> ComparisonSign`
    - Compiler generates: `__lt__`, `__le__`, `__gt__`, `__ge__` from `__cmp__`
- **Crashable:** ONE method - `@readonly routine Me.message() -> Text<letter32>` (RazorForge) / `-> Text` (Suflae)
    - Used for error types
    - `throw` keyword auto-generates stack trace and debug info
- **Equality:** User implements `__eq__`, compiler generates `__ne__`
    - `__ne__` is ALWAYS compiler-generated (never user-defined)

**Implementation Priority:**

1. Parser support (protocol declarations)
2. Abstract methods (contract verification)
3. Mutation annotations (integrate with #3 mutation inference)
4. Default implementations
5. Generic constraints
6. Operator auto-generation (`__ne__` from `__eq__`, comparison ops from `__cmp__`)

**Depends On:**

- Method Mutation Inference (Immediate Priorities #3)
- Iterator Permission Inference (for migratable detection)

**See Also:
** [RazorForge-Protocols.md](../wiki/RazorForge-Protocols.md), [Suflae-Protocols.md](../wiki/Suflae-Protocols.md)

---

### Memory Model & Tokens

**Status:** ⏳ **DESIGN COMPLETE, INFERENCE MISSING**

**Handle Types (Storable/Returnable):** Owned entities, `Retained<T>`, `Tracked<T>`, `Shared<T>`, `Snatched<T>`

**Token Types (Non-storable):** `Viewed<T>` (readonly), `Hijacked<T>` (mutable), `Inspected<T>` (shared readonly),
`Seized<T>` (shared mutable)

**Key Distinction:** Handles can be stored in fields, returned from routines, and used as variant payloads. Tokens
cannot.

**Blocking Issue:** Requires Method Mutation Inference (Immediate Priorities #3)

**Additional Work Needed:**

- Iterator Permission Inference (R/RW/RWM detection)
- Migratable operation detection (DynamicSlice buffer relocations)
- Token type enforcement at call sites
- Return type wrapping for token propagation
- Variant payload validation (reject tokens, allow handles)

**See Also:**

- [RazorForge Memory Model](../wiki/RazorForge-Memory-Model.md) - Core single-threaded memory management
- [RazorForge Handles vs Tokens](../wiki/RazorForge-Handles-vs-Tokens.md) - Handle/token distinction and properties
- [RazorForge Variants](../wiki/RazorForge-Variants.md) - Variant payload restrictions

---

## FUTURE WORK (Lower Priority)

### Parser Features

#### Imported Declarations (FFI)

```razorforge
imported("C") routine printf(format: cstr, ...) -> cint
```

**Tasks:**

- [ ] Parse `imported` keyword with calling convention
- [ ] Parse variadic `...` parameter
- [ ] Handle function signature without body

---

#### Preset (Compile-Time Constants)

```razorforge
preset PI: f64 = 3.14159265359
```

**Tasks:**

- [ ] Parse `preset` keyword
- [ ] Store in symbol table as compile-time constant
- [ ] Inline at use sites

---

#### Common (Static Methods)

```razorforge
record Point {
    common routine origin() -> Point { ... }
}
```

**Tasks:**

- [ ] Parse `common` modifier before `routine`
- [ ] Mark as static/class-level in AST
- [ ] Code generation without receiver

---

#### Inheritance Control

```razorforge
sealed entity FinalClass { ... }
entity Animal {
    open routine speak() -> Text { ... }
}
entity Dog from Animal {
    override routine speak() -> Text { ... }
}
```

**Tasks:**

- [ ] Add `open`, `sealed`, `override` to lexer
- [ ] Parse modifiers in correct positions
- [ ] Validate override relationships

---

#### Scoped Access Syntax

```razorforge
hijacking doc as d { d.edit() }
viewing doc as d { print(d.title) }
seizing mutex as m { m.value = 10 }
```

**Tasks:**

- [ ] Parse `hijacking`, `viewing`, `seizing`, `inspecting`, `using`
- [ ] Parse `as` binding
- [ ] Semantic analysis: verify access semantics
- [ ] Code generation: generate locking/unlocking

---

#### Record `with` Expression (Lens Syntax)

**Status:** 🔴 **NOT IMPLEMENTED** - Documented but missing from compiler

**Problem:** Records are immutable but have no way to create modified copies. The `with` expression is documented as the
primary way to update record fields.

```razorforge
record Point {
    x: f32
    y: f32
}

let p1 = Point(x: 1.0, y: 2.0)
let p2 = p1 with (x: 99.0)  # Creates Point(x: 99.0, y: 2.0)

# Nested record updates with lens dot-notation
record Address {
    street: ValueText<64>
    city: ValueText<32>
    zip: u32
}

record Person {
    name: ValueText<32>
    age: u32
    address: Address
}

let person = Person(
    name: "Alice",
    age: 30,
    address: Address(street: "123 Main St", city: "Springfield", zip: 12345)
)

# Update nested field
let moved = person with (address.city: "Shelbyville")
let updated = person with (age: 31, address.street: "456 Oak Ave")
```

**Syntax:**

- `record with (field: value)` - Single field update
- `record with (field1: value1, field2: value2)` - Multiple fields
- `record with (nested.field: value)` - Nested field with lens dot-notation

**Tasks:**

- [ ] Add `with` keyword to lexer (may already exist)
- [ ] Parse `<expr> with (<field_updates>)` syntax
- [ ] Create `WithExpression` AST node
- [ ] Parse field update list: `field: value` separated by commas
- [ ] Parse lens dot-notation for nested fields: `address.city: value`
- [ ] Semantic analysis: validate record type, field names, field types
- [ ] Code generation: create new record with updated fields
- [ ] Optimize: copy only changed fields, reuse unchanged data
- [ ] Type checking: ensure field values match field types

**Design Notes:**

- Works only on records (immutable value types)
- Creates a new record, original is unchanged
- Lens dot-notation allows deep updates without manual nesting
- All field names must exist in the record type
- Cannot add new fields, only update existing ones

**Files:**

- `src/Lexer/TokenType.cs` - Add `With` token if needed
- `src/Lexer/RazorForgeTokenizer.cs` - Add "with" keyword
- `src/Lexer/SuflaeTokenizer.cs` - Add "with" keyword
- `src/AST/Expressions.cs` - Add `WithExpression` node
- `src/Parser/RazorForgeParser.Expressions.cs` - Parse with expression
- `src/Analysis/SemanticAnalyzer.Expressions.cs` - Type check and validate
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Generate record copy with updates

**See Also:** [RazorForge Records](../wiki/RazorForge-Records.md) - Lines 178-263 (with statement
documentation), [Suflae Records](../wiki/Suflae-Records.md)

---

#### Conditional Expression (if-then-else)

```razorforge
let result = if condition then value_a else value_b
let status = if score >= 60 then "Pass" else "Fail"
```

**Purpose:** Inline conditional values for simple cases (avoid verbose if-else blocks)

**Tasks:**

- [ ] Add `then` keyword to lexer (currently not reserved)
- [ ] Parse `if <expr> then <expr> else <expr>` as expression
- [ ] Create `ConditionalExpression` AST node
- [ ] Type checking: both branches must return compatible types
- [ ] Code generation: use phi nodes or select instruction
- [ ] Validate: condition must be boolean type
- [ ] Restriction: Keep simple - no multi-line, no chaining

**Design Notes:**

- Different from `if is` type narrowing (that's for pattern matching)
- Different from statement `if`/`unless` (those don't produce values)
- Similar to ternary operator in C/JavaScript but more readable
- Should work with Result/Lookup: `let val = if result is Crashable e then 0 else result`

**Files:**

- `src/Lexer/TokenType.cs` - Add `Then` token
- `src/Lexer/RazorForgeTokenizer.cs` - Add "then" keyword
- `src/Lexer/SuflaeTokenizer.cs` - Add "then" keyword
- `src/AST/Expressions.cs` - Add `ConditionalExpression` node
- `src/Parser/RazorForgeParser.Expressions.cs` - Parse conditional expression
- `src/Analysis/SemanticAnalyzer.Expressions.cs` - Type check branches
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Generate phi/select

---

#### None Variant Case (T? and Lookup<T>)

**Design Decision:** ✅ **CONFIRMED** - Use capitalized `None` as variant case

**Rationale:**

- `None` is a variant case, not a primitive literal
- `T?` is sugar for `variant Optional<T> { Value(T), None }`
- `Lookup<T>` has three cases: `Value(T)`, `Crashable(error)`, `None`
- Variant cases are capitalized: `SUCCESS`, `ERROR`, `None`
- Keeps design consistent with variant pattern matching

**Usage:**

```razorforge
# Optional (T?) - None represents absence
let maybe_value: s32? = None   # None as constructor
when maybe_value {
    is None => show("Nothing")   # Pattern match on None case
    is value => show("Got: {value}")
}

# Lookup<T> - None represents "not found" (vs Crashable = error)
let result: Lookup<User> = find_user(id: 42)
when result {
    is None => show("Not found")          # None = absent
    is Crashable error => show(error)     # Crashable = error
    is user => show("Found: {user}")      # Value case
}

# Variant shorthand - branches with no payload
variant FileResult {
    SUCCESS(data: Text)
    NOT_FOUND              # Shorthand for NOT_FOUND(None)
    ERROR(details: Text)
}
```

**Distinction from `Blank`:**

- **`Blank`** = unit type for function returns (like `void` in C)
- **`None`** = variant case for absence in `T?`/`Lookup<T>`

**Tasks:**

- [ ] Add `None` as keyword/identifier (may already exist)
- [ ] Parse `None` as variant constructor (zero-argument)
- [ ] Type checking: `None` infers to `T?` based on context
- [ ] Code generation: represent as tagged union with None tag
- [ ] Pattern matching: `is None` matches absence case
- [ ] Update variant shorthand: `BRANCH` same as `BRANCH(None)`
- [ ] Semantic analysis: validate `None` usage in optional contexts

**Files:**

- `src/Lexer/TokenType.cs` - Add `None` token if needed
- `src/Lexer/RazorForgeTokenizer.cs` - Add "None" keyword
- `src/Lexer/SuflaeTokenizer.cs` - Add "None" keyword
- `src/AST/Expressions.cs` - Ensure variant constructor handles None
- `src/Parser/RazorForgeParser.Expressions.cs` - Parse None constructor
- `src/Analysis/SemanticAnalyzer.Expressions.cs` - Type inference for None
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Generate None as variant tag

**See Also:
** [RazorForge Variants](../wiki/RazorForge-Variants.md), [Suflae Variants](../wiki/Suflae-Variants.md), [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md)

---

#### String Literal Enhancements

**Status:** ⏳ **PARTIALLY IMPLEMENTED** (regular and raw strings work)

**Missing:**

- ❌ Backslash continuation - `"Long text \<newline>continues"` (needed for 80-char limit)
- ❌ `dedent()` function - Strip common leading whitespace from multi-line strings

**Backslash Continuation:**

```razorforge
let error = "Error: The connection failed \
because the credentials were invalid."
# Result: "Error: The connection failed because the credentials were invalid."
```

**Tasks:**

- [ ] Lexer: Detect `\` followed by newline
- [ ] Lexer: Remove `\`, newline, and leading whitespace from next line
- [ ] Test with various scenarios

**dedent() Function:**

```razorforge
let message = dedent("
    Welcome to RazorForge!
    Commands:
      build  - Build project
      run    - Run project
    ")
```

**Tasks:**

- [ ] Implement in stdlib: `Text.dedent(me: Text) -> Text`
- [ ] Algorithm: find min indent, strip from all lines
- [ ] Test cases for edge cases

**Files:**

- `src/Lexer/BaseTokenizer.cs` - Backslash continuation
- `stdlib/Text/Text.rf` - dedent() method

**See Also:** [Code Style - Line Length Limit](../wiki/RazorForge-Code-Style.md#line-length-limit)

---

### Lambda Code Generation

**Status:** ⏳ **PARSING WORKS, CODEGEN INCOMPLETE**

**Current State:**

- ✅ Arrow lambda parsing: `x => x * 2`, `(a, b) => a + b`
- ✅ AST nodes created correctly
- ❌ Code generation not implemented

**Language Restriction:** ⚠️ Multiline lambdas are BANNED (use named functions instead)

```razorforge
# ✅ OK
let double = x => x * 2
let sum = (a, b) => a + b
let result = list.select(x => x * 2)

# ❌ BANNED
let compute = x => {
    let temp = x * 2
    temp + 1
}
```

**Tasks:**

- [ ] Generate function pointer type for lambda
- [ ] Create anonymous function in LLVM IR
- [ ] Handle captures (closure semantics)
- [ ] Implement `Routine<(T), U>` type
- [ ] Enforce single-expression restriction in parser

**Files:**

- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - `VisitLambdaExpression()`
- `src/Parser/RazorForgeParser.Expressions.cs` - Validate no block syntax

---

### Auto-Generated Methods for Data Types

Compiler should auto-generate default methods for:

**Record:**

- `to_text()`, `to_debug()`, `memory_size()`, `hash()`, `==`, `!=`, `is`, `isnot`

**Entity:**

- `to_text()`, `to_debug()`, `memory_size()`, `id()`, `copy()`, `==`, `!=`, `===`, `!==`

**Choice:**

- `to_text()`, `hash()`, `to_integer()`, `all_cases()`, `from_text()`, `==`, `!=`

**Variant:**

- ❌ **NO METHODS OR OPERATORS** - Variants have ABSOLUTELY NO methods or operators
- ❌ No `to_text()`, `to_debug()`, `hash()`, `==`, `!=`, or any other methods
- ✅ **ONLY pattern matching with `when`** is allowed

**Important:** Variants are **local-only random boxes** for immediate unpacking:

- Can hold: records, residents (RazorForge only), entities, **handles** (Retained<T>, Shared<T>, Tracked<T>,
  Snatched<T>), or None
- Cannot hold: **tokens** (`Viewed<T>`, `Hijacked<T>`, `Inspected<T>`, `Seized<T>`) or nested variants
- Usage: Can return from functions (producer-consumer) - cannot store in fields, pass as parameters, copy, or reassign
- Philosophy: Create locally, pattern match immediately, extract data, done. Not containers—unpacking tools.
- **No API:** You're supposed to immediately extract the payload and work with that. The payload has all the methods you
  need.

See [RazorForge Variants](../wiki/RazorForge-Variants.md)
and [RazorForge Handles vs Tokens](../wiki/RazorForge-Handles-vs-Tokens.md).

**Files:**

- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - Add auto-generation logic

---

### Language Design Changes (TODO)

#### 0. Variable Naming Convention & Compiler-Generated Temps

**Status:** ✅ **COMPLETE** (2025-12-16) - Leading underscores removed

**Decision:** Leading underscores removed from all user-visible variable names. Compiler-generated temporaries use
double underscore prefix.

**Rationale:**

- Cleaner user code: `varname` instead of `_varname`
- Clear distinction between user and compiler variables
- Double underscore prefix reserved for compiler internals

**Convention:**

- User variables: `varname`, `count`, `result`
- Compiler temporaries: `__varname`, `__b`, `__c`

**Example:**

```razorforge
# Comparison chain with compiler-generated temps
if a < b < c {
    # Compiler generates: __b = b, __c = (a < __b) and (__b < c)
}
```

**Completed:**

- [x] Updated variable naming convention
- [x] Established `__` prefix for compiler temps
- [x] Updated in comparison chain implementation
- [x] Documented in code examples

---

#### 0a. Remove `usurping` Keyword

**Status:** ✅ **COMPLETE** (2025-12-16) - Removed from language

**Decision:** The `usurping` keyword has been removed from RazorForge. Ownership transfer is already implicit when
passing handles by value - no special keyword needed.

**Rationale:**

- Redundant: Normal ownership semantics already express the behavior
- Confusing: Doesn't clarify "which parameter survives"
- Already implicit: Function signature shows consumption

**Example:**

```razorforge
# This already works without usurping:
routine pick_longest(a: Retained<Text<letter32>>, b: Retained<Text<letter32>>)
    -> Retained<Text<letter32>> {
    if a.view().length > b.view().length {
        return a  # b is dropped
    } else {
        return b  # a is dropped
    }
}
```

**Completed:**

- [x] Removed from wiki/Keyword-Comparison.md
- [x] Removed from LSP syntax highlighting
- [x] Removed from LSP keyword completions
- [x] Removed from VS Code extension

**Files Changed:**

- `wiki/Keyword-Comparison.md`
- `extensions/razorforge/syntaxes/razorforge.tmLanguage.json`
- `src/LanguageServer/RazorForgeCompilerService.cs`

---

#### 1. Entry Point: `main` → `start`

**Status:** ⏸️ **TODO** - Design decision confirmed, implementation needed

**Change:** Rename entry point function from `main` to `start`

**Rationale:**

- Consistency with Suflae (`routine start()`)
- Avoid confusion with C's `main()`
- More intuitive for modern language

**Tasks:**

- [ ] Update compiler to look for `start()` instead of `main()`
- [ ] Update all examples and documentation
- [ ] Add migration note for existing code

---

#### 2. Reserve `start` and `start!` as Keywords

**Status:** ⏸️ **TODO**

**Restriction:** Prevent user code from defining routines named `start` or `start!` (except entry points)

**Tasks:**

- [ ] Add semantic check: reject `start`/`start!` declarations outside entry point
- [ ] Generate clear error message
- [ ] Allow in entry point file only

---

#### 3. `@crash_only` Attribute for `start!`

**Status:** ⏸️ **TODO**

**Requirement:** Crashable entry points must use `@crash_only` attribute

```razorforge
@crash_only
routine start!() {
    let file = open_file!("data.txt")  # Can use crashable calls
}
```

**Tasks:**

- [ ] Parse `@crash_only` attribute
- [ ] Validate only on `start!` functions
- [ ] Enable crashable calls in attributed functions
- [ ] Error if `start!` missing attribute

---

#### 4. Inline Conditional Restrictions

**Status:** ✅ **COMPLETE** (2025-12-08)

**Restrictions:**

- ❌ No block syntax in inline conditionals
- ❌ No nested inline conditionals

```razorforge
# ✅ OK
let x = if count > 0 then "items" else "empty"

# ❌ CE: Cannot use blocks
let y = if cond then { compute() } else { default() }

# ❌ CE: Cannot nest
let z = if a then (if b then 1 else 2) else 3
```

**Completed:**

- [x] Parser restrictions enforced
- [x] Clear compile errors generated
- [ ] Documentation updates (pending)

---

#### 5. Ban In-Scope Method Declarations

**Status:** ⏸️ **TODO** - Design decision confirmed

**Restriction:** Methods MUST be declared outside type scope

```razorforge
# ✅ OK
public entity List<T> {
    private var _buffer: pointer
}

routine List<T>.push(value: T) {
    # Implementation
}

# ❌ CE: In-scope methods not allowed
public entity List<T> {
    routine push(value: T) {  # Error!
        # ...
    }
}
```

**Rationale:**

- Consistency (internal and extension methods use same syntax)
- Multi-file organization (methods can be split across files)
- Cleaner type definitions
- Simpler parser

**Tasks:**

- [ ] Update parser to reject in-scope method declarations
- [ ] Generate clear error message
- [ ] Apply to entity/resident/record
- [ ] Update documentation

---

#### 6. Suflae `eternal` Keyword

**Status:** ⏸️ **TODO** (Suflae only)

**Feature:** Application-scoped singleton actors

```suflae
# start.sf
eternal AppCore:
    logger: Logger
    config: Config
    metrics: Metrics

routine start():
    AppCore.logger = Logger()
    # ...
```

**Requirements:**

- Start file only
- Single declaration per application
- Actor semantics (message passing)
- Immortal lifetime (never GC'd)

**Tasks:**

- [ ] Add `eternal` keyword to Suflae lexer
- [ ] Parse `eternal TypeName:` syntax
- [ ] Validate rules (single, start file only)
- [ ] Generate actor wrapper code
- [ ] Make globally accessible

**Files:**

- `src/Lexer/SuflaeLexer.cs`
- `src/Parser/SuflaeParser.cs`
- `src/Analysis/SemanticAnalyzer.cs`
- `src/CodeGen/SuflaeCodeGenerator.cs`

**See:** [Suflae Eternal](../wiki/Suflae-Eternal.md), [Resident vs Eternal](../wiki/Resident-vs-Eternal.md)

---

## KNOWN BUGS

### Bug 12.7: Type Constructor Calls

```razorforge
routine u8.to_saddr(me: u8) -> saddr {
    return saddr(me)  # Generates function call instead of type conversion!
}
```

**Fix:** Detect type constructor calls and generate appropriate conversion intrinsics.

---

### Bug 12.10: Integer Literal Type Inference

```razorforge
routine double_it(x: s32) -> s32 {
    return x * 2  # Error: s32 * s64 (literal defaults to s64)
}
```

**Fix:** Propagate expected type to literal expressions during semantic analysis.

---

### Bug 12.11: Function Call Return Type

```razorforge
routine get_value() -> s64 { return 42 }
let x: s64 = get_value()  # Type mismatch: expected s64, got i32
```

**Fix:** Look up actual return type from function signature.

---

### Bug 12.12: Dunder Method Variant Names

```razorforge
routine s32.__add__!(other: s32) -> s32 { ... }
# Should generate: try_add, check_add
# Currently generates: try___add__, check___add__
```

**Fix:** Strip `__` prefix/suffix before adding variant prefix.

---

### Bug 12.14: Error Location Tracking

**Status:** ✅ **FIXED** (2025-12-08)

Error messages now show correct file and line numbers using AST node locations.

---

### Bug 12.15: Module Resolution

**Problem:** Import statements fail to resolve modules even with `--stdlib` flag.

```razorforge
import Console  # Error: Module not found
```

**Fix:** Debug `ModuleResolver.cs` path resolution and stdlib flag handling.

**Files:**

- `src/Analysis/ModuleResolver.cs`
- `src/CLI/Program.cs`
- `src/Analysis/SemanticAnalyzer.Declarations.cs`

---

### Bug 12.16: Pointer Type in Binary Expressions

**Status:** ✅ **FIXED** (2025-12-04)

Added pointer type detection before calling `GetIntegerBitWidth()`.

---

### Bug 12.17: Optional Field Syntax

**Problem:** Parser rejects `?` syntax for optional fields.

```razorforge
entity SortedDict<K, V> {
    private root: BTreeDictNode<K, V>?  # Parse error!
}
```

**Fix:** Extend `ParseTypeExpression()` to recognize `?` suffix.

**Files:**

- `src/Parser/RazorForgeParser.Expressions.cs`
- `src/AST/ASTNode.cs` - Add `IsOptional` flag
- `src/Analysis/SemanticAnalyzer.Types.cs`
- `src/CodeGen/LLVMCodeGenerator.Types.cs`

**Workaround:** Entity references are nullable by default without explicit `?`

---

## REFERENCE INFORMATION

### File Locations

**Core Compiler:**

- `src/Parser/RazorForgeParser.cs` - Main parser
- `src/Analysis/SemanticAnalyzer.cs` - Semantic analysis and type checking
- `src/CodeGen/LLVMCodeGenerator.cs` - LLVM IR code generation
- `src/Analysis/GenericTypeResolver.cs` - Generic template matching (NEW, 256 lines)
- `src/Analysis/CorePreludeLoader.cs` - Auto-load core namespace (NEW, 100 lines)

**Module System:**

- `src/Analysis/ModuleResolver.cs` - Module path resolution (BROKEN)
- `src/Analysis/SymbolTable.cs` - Symbol tracking

**Standard Library:**

- `stdlib/core/` - Auto-loaded types (primitives, Maybe, Result, etc.)
- `stdlib/Collections/` - List, Dict, Set, etc.
- `stdlib/Text/` - Text type and operations
- `stdlib/Console.rf` - Console I/O

**Native Runtime:**

- `native/runtime/` - C implementations of native functions
- `native/build.sh` - Build script

---

### Implementation Patterns

#### Extract-Operate-Wrap Pattern

```llvm
; Extract primitive from record
%tmp1 = extractvalue %s32 %value, 0
; Operate on primitive
%tmp2 = add i32 %tmp1, 42
; Wrap result back into record
%tmp3 = insertvalue %s32 undef, i32 %tmp2, 0
```

#### Generic Type Instantiation

1. `InstantiateGenericRecord` queues types instead of generating immediately
2. `GeneratePendingTypeInstantiations` uses while loop to process queue
3. Handles recursive dependencies (e.g., `Range<BackIndex<uaddr>>`)
4. All types generated before any functions

#### Mutation Inference (Planned)

1. Direct mutation detection - Walk AST for field writes
2. Call graph construction - Track who-calls-whom
3. Transitive propagation - Topological sort + marking
4. Metadata caching - Store in TypeInfo, cache in compiled artifacts

---

### Related Documentation

**Language Design:**

- [GENERICS-STATUS.md](GENERICS-STATUS.md) - Comprehensive generics analysis
- [BUG-12.13-ANALYSIS.md](BUG-12.13-ANALYSIS.md) - Generic method template matching
- [RazorForge-Protocols.md](../wiki/RazorForge-Protocols.md) - Protocol system spec
- [Modules-and-Imports.md](../wiki/Modules-and-Imports.md) - Module system (NaaM)

**Memory Model:**

- [RazorForge Memory Model](../wiki/RazorForge-Memory-Model.md) - Single-threaded memory management
- [RazorForge Handles vs Tokens](../wiki/RazorForge-Handles-vs-Tokens.md) - Handle/token distinction and properties
- [RazorForge Concurrency Model](../wiki/RazorForge-Concurrency-Model.md) - Multi-threaded memory management
- [RazorForge Routines](../wiki/RazorForge-Routines.md) - Method declarations
- [RazorForge Variants](../wiki/RazorForge-Variants.md) - Tagged unions and restrictions

**Code Style:**

- [RazorForge Code Style](../wiki/RazorForge-Code-Style.md) - Style guide
- [Suflae Code Style](../wiki/Suflae-Code-Style.md) - Suflae style guide

**Suflae-Specific:**

- [Suflae Eternal](../wiki/Suflae-Eternal.md) - `eternal` keyword
- [Resident vs Eternal](../wiki/Resident-vs-Eternal.md) - Comparison

---

## RECENTLY COMPLETED WORK

### Getter/Setter Visibility Modifiers (2025-12-19)

**Status:** ✅ **COMPLETED** - `public private(set)` syntax implemented in both parsers

**Feature Description:**
- Added support for separate getter and setter visibility modifiers on fields
- Syntax: `public private(set) var field: Type`
- Allows public read with restricted write access

**Implementation:**

1. **AST Changes:**
   - Updated `VariableDeclaration` record with optional `SetterVisibility` parameter
   - Getter visibility uses existing `Visibility` field
   - Setter visibility defaults to `null` (same as getter)

2. **RazorForge Parser:**
   - Added `ParseGetterSetterVisibility()` method in `RazorForgeParser.Declarations.cs`
   - Parses syntax: `public private(set)`, `public internal(set)`, etc.
   - Updated `ParseVariableDeclaration()` and `ParseFieldDeclaration()`
   - Updated main parser dispatch in `RazorForgeParser.cs`

3. **Suflae Parser:**
   - Added `ParseGetterSetterVisibility()` method in `SuflaeParser.Declarations.cs`
   - Same syntax support as RazorForge
   - Updated `ParseVariableDeclaration()`
   - Updated main parser dispatch in `SuflaeParser.cs`

4. **Validation:**
   - Enforces visibility hierarchy: `private < family < internal < public`
   - Setter must be equal or more restrictive than getter
   - Compile error for invalid combinations like `private public(set)`

**Valid Combinations (10 total):**
```razorforge
public private(set) var x    # Public read, private write (most common)
public internal(set) var y   # Public read, module write
public family(set) var z     # Public read, inheritor write
internal private(set) var a  # Module read, private write
internal family(set) var b   # Module read, inheritor write
family private(set) var c    # Inheritor read, private write
public var d                 # Fully public (getter and setter)
internal var e               # Fully internal
family var f                 # Fully family
private var g                # Fully private
```

**Build Status:** ✅ Compiles successfully, parser tests pass

**Files Modified:**
- `src/AST/Declarations.cs` (added SetterVisibility parameter)
- `src/Parser/RazorForgeParser.cs` (updated dispatch)
- `src/Parser/RazorForgeParser.Declarations.cs` (added parsing logic)
- `src/Parser/SuflaeParser.cs` (updated dispatch)
- `src/Parser/SuflaeParser.Declarations.cs` (added parsing logic)

---

### Parser and Memory System Improvements (2025-12-18)

**Status:** ✅ **COMPLETED** - Nested generics, operators, FFI reorganization, memory wrapper refactor

**Parser Enhancements:**
1. **Nested Generic Support:**
   - Fixed `>>` token splitting in `ConsumeGreaterForGeneric()`
   - Now correctly parses `Tracked<Retained<T>>`, `List<List<T>>`, etc.
   - Updated 3 parser files: RazorForgeParser.Expressions.cs (2 locations), RazorForgeParser.Helpers.cs

2. **Operator Support:**
   - Added `notfrom` - negated inheritance check
   - Added `notfollows` - negated protocol check
   - Operators already existed in lexer, just needed parser support

3. **Type System Fixes:**
   - Fixed iterator types: changed 10 iterators from `record` to `entity`
   - Records cannot contain entities - iterator types were violating this rule

**Memory System Refactor:**
1. **FFI Reorganization:**
   - Deleted `stdlib/memory/CSubsystem.rf`
   - Moved Snatched<T> operations to `Snatched.rf` (8 FFI functions)
   - Moved DynamicSlice operations to `DynamicSlice.rf` (5 FFI functions + legacy uaddr versions)
   - Better code locality, no central coupling

2. **Mixed Architecture Applied:**
   - **Principle:** `uaddr` for fixed-type metadata, `Snatched<T>` for generic user data
   - Updated 8 memory wrapper files:
     - ✅ Tracked.rf - Weak references (uaddr for controllers, Snatched<T> for data)
     - ✅ Retained.rf - Single-threaded RC (uaddr for controller, Snatched<T> for data)
     - ✅ Shared.rf - Thread-safe RC (uaddr for controller/lock, Snatched<T> for data)
     - ✅ Seized.rf - Write lock token (uaddr for lock guard, Snatched<T> for ptr)
     - ✅ Inspected.rf - Read lock token (uaddr for lock guard, Snatched<T> for ptr)
     - ✅ Viewed.rf - Read-only view (Snatched<T> for ptr)
     - ✅ Hijacked.rf - Exclusive access (Snatched<T> for ptr)
     - ✅ Snatched.rf - Raw pointer wrapper (uaddr internally, type-safe API)

3. **Type Safety Benefits:**
   - Generic user data (`T`) protected by Snatched<T>
   - Internal metadata kept simple with uaddr
   - Clear architectural boundary: user data vs bookkeeping
   - No verbose read-modify-write for controller operations

**Parser Workarounds:**
- Used `@intrinsic.store<T>()` for multi-parameter FFI calls (parser limitation)
- Used manual field assignment for nested generic constructors (parser issue with named args)

**Build Status:** ✅ 0 errors, 0 warnings

**Files Modified:**
- Parser: 3 files
- Stdlib: 18 memory files, 10 iterator files
- All memory wrapper files now parse successfully

---

### Standard Library Method Refactoring (2025-12-18)

✅ **STDLIB CODE STYLE CLEANUP** - All methods moved outside type scopes

**Refactored Files:**

- Error handling: Maybe.rf, Result.rf, Lookup.rf, DataHandle.rf
- Memory types: DynamicSlice.rf, MemorySize.rf
- Native types: Blank.rf, None.rf
- Error types: error.rf, common.rf, IndexOutOfBoundsError.rf, DivisionByZeroError.rf, IntegerOverflowError.rf,
  NegativeExponentError.rf, MemoryError.rf, NotImplementedError.rf, IndeterminateResultError.rf

**Pattern Changed:**

```razorforge
# Before (INCORRECT):
record Point {
    x: f32
    routine distance() -> f32 { ... }  # ❌ In-scope
}

# After (CORRECT):
record Point {
    x: f32
}
routine Point.distance() -> f32 { ... }  # ✅ Extension style
```

**Impact:**

- 16 files refactored across stdlib/
- All files compile successfully (0 errors, 0 warnings)
- Consistent with language design (COMPILER-TODO.md section "Ban In-Scope Method Declarations")
- Parser can now safely reject in-scope method declarations

**Files Modified:**

- `stdlib/ErrorHandling/*.rf` (4 files)
- `stdlib/memory/DynamicSlice.rf, MemorySize.rf` (2 files)
- `stdlib/NativeDataTypes/Blank.rf, None.rf` (2 files)
- `stdlib/errors/*.rf` (8 files)

---

### Method "me" Parameter and Numeric Literal Fixes (2025-12-18)

✅ **CRITICAL COMPILER FIXES** - Methods and numeric literals now work correctly

**Fixed Issues:**

- ✅ **Implicit "me" parameter** - Methods (functions with dots like `f64.__add__`) now have implicit "me" in scope
    - Detects receiver type from function name (e.g., `f64` from `f64.__add__`)
    - Registers "me" as VariableSymbol with correct type
    - Fixes "Undefined identifier 'me'" errors in stdlib
- ✅ **Numeric literal underscores in exponents** - Scientific notation with suffixes now works (e.g., `2.2e-16_f64`)
    - Fixed BaseTokenizer.ScanNumber() to allow underscores after exponent
    - Prevents lexer from treating suffix as separate identifier

**Impact:**

- All stdlib native type methods compile without errors
- IEEE 754 floating-point constants work correctly
- Operator overloading methods can use "me" parameter

**Files Modified:**

- `src/Analysis/SemanticAnalyzer.Declarations.cs` - Lines 277-293 (implicit "me" registration)
- `src/Lexer/BaseTokenizer.cs` - Line 205 (underscore in exponent)

**Example:**

```razorforge
# Before: ERROR - "Undefined identifier 'me'"
routine f64.__add__(other: f64) -> f64 {
    danger! {
        return @intrinsic.add.wrapping<double>(me, other)  # ❌ me not defined
    }
}

# After: WORKS - "me" implicitly available
routine f64.__add__(other: f64) -> f64 {
    danger! {
        return @intrinsic.add.wrapping<double>(me, other)  # ✅ me has type f64
    }
}

# Before: ERROR - "_f64" treated as identifier
preset F64_EPSILON: f64 = 2.220446049250313e-16_f64  # ❌ Lexer error

# After: WORKS - suffix parsed correctly
preset F64_EPSILON: f64 = 2.220446049250313e-16_f64  # ✅ Token: F64Literal
```

---

### Generic Type System (2025-12-04)

✅ **MAJOR MILESTONE** - End-to-end generics fully working!

- Generic type instantiation (`TestType<s64>`)
- Generic method calls (`instance.method()`)
- Template matching system (GenericTypeResolver.cs)
- Full type safety with compilation

**See:** [GENERICS-STATUS.md](GENERICS-STATUS.md), [BUG-12.13-ANALYSIS.md](BUG-12.13-ANALYSIS.md)

---

### Record Type System Consistency (2025-12-06)

✅ Systematic fixes ensuring ALL types use record wrappers (`%s32`, `%u64`, etc.)

- Core type mapping consistency
- When expression return values
- Bitcast intrinsic
- Decimal type mappings
- Arithmetic operations
- Boolean operations
- Integer literal wrapping

---

### Core Prelude Auto-Loading (2025-12-08)

✅ Automatic loading of all `namespace Core` files

- No more `import s64` or `import Maybe`
- Transitive dependency loading
- Recursive stdlib scanning
- Clean user code

**Files:** `src/Analysis/CorePreludeLoader.cs` (NEW)

---

### Comprehensive Code Generator Refactoring (2025-12-08)

✅ **MAJOR OVERHAUL** - Numerous edge cases and enhancements

- Location tracking with `UpdateLocation()` method (Bug 12.14 fix)
- Generic type full name handling
- Type conversion and casting
- Binary expression enhancements
- Floating-point constant handling
- Function generation improvements
- Generic template matching
- Method call enhancements
- Native function declaration tracking
- Record field type tracking

---

### LLVM ABI and Control Flow Fixes (2025-12-08)

✅ Multi-field struct argument passing
✅ Control flow termination
✅ Generic entity method instantiation
✅ Proper stack allocation for struct values used as pointers

---

### Inline Conditional Restrictions (2025-12-08)

✅ Parser restrictions enforced
✅ No block syntax in inline conditionals
✅ No nested inline conditionals

---

### Wiki Documentation Syntax Standardization (2025-12-17)

✅ **COMPREHENSIVE SYNTAX CLEANUP** - All wiki documentation standardized

**Comment Syntax:**

- ✅ Changed all `//` comments to `#` in RazorForge/Suflae code examples
- ✅ Removed all `/* */` block comments, converted to `#`
- ✅ Preserved `###` for doc comments
- ✅ Fixed incorrectly converted division operators (`a // b` not comments)

**Annotation Syntax:**

- ✅ Changed `#[attribute]` to `@[attribute]` syntax
- ✅ Updated examples: `@[no_mangle]`, `@[readonly]`, etc.

**Keyword Updates:**

- ✅ Changed access modifier `local` → `internal` (module-wide access)
- ✅ Changed FFI keyword `external` → `imported` (foreign functions)
- ✅ Updated all wiki files with keyword changes (25+ files)

**Field Declaration Syntax:**

- ✅ Added `var`/`let` keywords to all entity/record/resident field declarations
- ✅ Use `let` for immutable fields (id, uuid, timestamp, config, etc.)
- ✅ Use `var` for mutable fields (default)
- ✅ Updated 54 files with 1,432 insertions

**Extension Method Syntax:**

- ✅ Moved all routine definitions outside entity/record scopes
- ✅ Changed from nested routines to extension syntax: `routine TypeName.method_name()`
- ✅ Updated 40+ wiki files to use correct extension method pattern

**@prelude Documentation:**

- ✅ Documented `@prelude` attribute behavior (no import, no namespace qualification)
- ✅ Updated all Console I/O examples to show correct @prelude usage
- ✅ Clarified that `show()`, `get_line()`, etc. are called WITHOUT `Console.` prefix
- ✅ Added comprehensive examples to Attributes.md and Modules-and-Imports.md

**Variant Return Semantics:**

- ✅ Updated variant documentation to allow returns (producer-consumer pattern)
- ✅ Changed from "cannot return" to "can return for producer-consumer"
- ✅ Updated RazorForge-Variants.md, Suflae-Variants.md, data type overviews
- ✅ Updated COMPILER-TODO.md line 628 to reflect new semantics

**Files Modified:** 77+ wiki files total

---

### Wiki Documentation Updates v2 (2025-12-17)

✅ **COMPREHENSIVE CORRECTNESS UPDATES** - All wiki documentation updated with critical corrections and missing features

**Visibility Syntax:**

- ✅ Fixed incorrect `@[public get, private set]` to correct `public private(set)` syntax
- ✅ Updated RazorForge-Code-Style.md and Suflae-Code-Style.md

**Record Type Correctness:**

- ✅ Fixed entity types in record fields: `Text` → `ValueText<letter32, SIZE>`
- ✅ Updated RazorForge-Error-Handling.md, RazorForge-Concurrency-Patterns.md, RazorForge-Variants.md
- ✅ Note: Records are value types and cannot contain entity types

**Entity Field Mutability:**

- ✅ Updated documentation emphasizing `var`/`let` requirement for entity fields
- ✅ Added hijacking scope examples showing immutable field protection

**Integer Division Behavior:**

- ✅ Corrected documentation: `Integer` type (arbitrary precision) DOES support `/` operator returning `Fraction`
- ✅ Native integers (`s32`, `u64`, etc.): `/` is CE, must use `//`
- ✅ Updated Numeric-Operators.md with comprehensive division operator table

**Error Handling Terminology:**

- ✅ Replaced "terminate" with "crash" throughout error handling docs
- ✅ Updated RazorForge-Error-Handling.md, Suflae-Error-Handling.md comparison tables

**Pattern Matching:**

- ✅ Documented that type prefixes can be omitted when variant type is known from context
- ✅ Added examples showing `is SUCCESS` vs `is HttpResponse.SUCCESS`

**Multiple Scoped Access:**

- ✅ Added comma-separated syntax documentation: `viewing a as av, b as bv { }`
- ✅ Updated RazorForge-Memory-Model.md with comprehensive examples

**Variant Methods:**

- ✅ Emphasized that variants have ABSOLUTELY NO methods or operators
- ✅ No `==`, `!=`, `to_text()`, `to_debug()`, `hash()` - ONLY pattern matching
- ✅ Updated RazorForge-Variants.md and Suflae-Variants.md

**Record Immutability:**

- ✅ Documented nested `with` expressions for nested record updates
- ✅ Explicitly stated that lens syntax is not supported

**Varargs Documentation:**

- ✅ Added comprehensive varargs sections to RazorForge-Routines.md and Suflae-Routines.md
- ✅ Rules: varargs MUST be first, only ONE varargs, all other params MUST be named
- ✅ Examples showing correct/incorrect usage

**Named Arguments Enforcement:**

- ✅ Updated from "Strong Recommendation" to "REQUIRED (Compile Error)"
- ✅ Documented that 2+ parameters REQUIRE named arguments (CE if positional)
- ✅ Updated RazorForge-Code-Style.md and Suflae-Code-Style.md

**Integer/Decimal/Fraction Implicit View:**

- ✅ Added section documenting implicit `.view()` for arithmetic operators
- ✅ Clarified that assignment still requires explicit `.retain()`/`.view()`/`.consume()`
- ✅ Updated Numeric-Types.md with comprehensive examples

**Lazy Iterator Scope Rules:**

- ✅ Documented that lazy iterators cannot escape their scope
- ✅ Must materialize with `.to_list()`, `.to_dict()`, `.to_set()` inside scope
- ✅ Added materialization methods table to Itertools.md

**to_text/to_debug Visibility:**

- ✅ Documented field visibility rules for default implementations
- ✅ `to_text()`: includes `public`, `internal`, `family` (user-facing)
- ✅ `to_debug()`: includes all fields including `private` (diagnostics)
- ✅ Both use constructor syntax: `TypeName(field: value, ...)`
- ✅ Updated RazorForge-Protocols.md and Suflae-Protocols.md

**Attribute Placement Style:**

- ✅ Confirmed existing documentation recommending attributes on separate lines
- ✅ Already documented in both RazorForge-Code-Style.md and Suflae-Code-Style.md

**Crashable Protocol:**

- ✅ Updated to show actual stdlib/errors/Crashable.rf contents with all required fields
- ✅ Added `message_handle`, `stack_trace`, `file_id`, `routine_id`, `line_no`, `column_no`, `error_code`
- ✅ Updated RazorForge-Error-Handling.md, Suflae-Error-Handling.md, RazorForge-Protocols.md, Suflae-Protocols.md

**Resident Type References:**

- ✅ Removed all mentions of "resident" from Suflae documentation (RazorForge-only feature)
- ✅ Updated Suflae-Variants.md, Suflae-Text.md, Suflae-Routines.md

**Record Field Mutability Keywords:**

- ✅ Removed all `var`/`let` keywords from record field declarations
- ✅ Records are immutable value types - fields don't need mutability keywords
- ✅ Only entities and residents use `var`/`let` for field mutability
- ✅ Updated 15 RazorForge wiki files with ~35 record definitions
- ✅ Pattern: `record Point { var x: s32 }` → `record Point { x: s32 }`

**ValueText Type Inference:**

- ✅ Simplified `ValueText<letter32, SIZE>` to `ValueText<SIZE>` throughout documentation
- ✅ `letter32` is the default encoding and can be inferred
- ✅ Preserved explicit encodings: `ValueText<letter16, SIZE>`, `ValueText<letter8, SIZE>`
- ✅ Updated 12 wiki files with ~44 occurrences
- ✅ Pattern: `ValueText<letter32, 128>` → `ValueText<128>`
- ✅ Applies to: record fields, entity fields, variables, type parameters, function signatures

**Variant Branch None Shorthand:**

- ✅ Variant branches with `None` payload can use shorthand notation
- ✅ Pattern: `NOT_FOUND(None)` → `NOT_FOUND` (no parentheses needed)
- ✅ Applies to both RazorForge and Suflae
- ✅ Example:
  ```razorforge
  variant FileResult {
      SUCCESS(data: FileData)
      NOT_FOUND              # Shorthand for NOT_FOUND(None)
      PERMISSION_DENIED      # Shorthand for PERMISSION_DENIED(None)
      ERROR(details: ErrorDetails)
  }
  ```
- ✅ Compiler should accept both forms: `BRANCH` and `BRANCH(None)`

**Record Lens Syntax:**

- ✅ Records now support lens syntax for immutable updates
- ✅ Lens syntax is the DEFAULT and PREFERRED way to define records
- ✅ Pattern: Records without `var`/`let` use lens syntax
- ✅ Example:
  ```razorforge
  record Point {
      x: s32
      y: s32
  }

  let p1 = Point(x: 10, y: 20)
  let p2 = p1 with (x: 30)  # Lens-based update
  ```
- ✅ Compiler should generate lens-based update methods for all record fields

**Files Modified:** 55+ wiki files (user-updated)

---

### Record Documentation Comprehensive Update (2025-12-17)

✅ **RECORD SEMANTICS AND RESTRICTIONS CLARIFIED**

**Lens Syntax Clarification:**

- ✅ Clarified that "lens syntax" refers to `with` expressions, NOT field declarations
- ✅ Renamed section: "Lens Syntax" → "Record Field Declaration Syntax"
- ✅ Updated section title: "The `with` Statement" → "The `with` Statement (Lens Syntax)"
- ✅ Pattern: `record with (field: value)` is lens syntax for immutable updates

**With Expression Syntax:**

- ✅ Fixed all occurrences: `with { }` → `with ( )` (parentheses, not braces)
- ✅ Nested updates use lens dot-notation: `record with (nested.field: value)`
- ✅ Applies to both RazorForge and Suflae

**Record Content Restrictions:**

- ✅ Records can ONLY contain:
    - Numeric types (RazorForge: u8-u128, s8-s128, f16-f128, d32-d128)
    - Numeric types (Suflae: s32, s64, s128, u32, u64, u128, f32, f64, d128)
    - Bool type
    - Other records
    - Fixed-size value types (RazorForge only: `ValueText<Letter, Size>`)
- ✅ Records CANNOT contain:
    - Entity types (`Text`, `Integer`, etc.)
    - Handles (`Retained<T>`, `Shared<T>`, `Tracked<T>`, `Snatched<T>`)
    - Tokens (`Viewed<T>`, `Hijacked<T>`, `Inspected<T>`, `Seized<T>`)
    - Any reference types

**Terminology Updates:**

- ✅ Changed "primitive types" → "numeric types" + "bool type"
- ✅ No "primitive types" exist in RazorForge/Suflae

**Suflae-Specific Updates:**

- ✅ Replaced invalid nested record example (Address/Person with Text/Integer entities)
- ✅ New example: Date/TimeStamp using only numeric types (u32)
- ✅ Fixed all u8 usage → u32 (Suflae doesn't have u8/u16/s8/s16/f16)

**Files Modified:**

- `wiki/RazorForge-Records.md`
- `wiki/Suflae-Records.md`

**Compiler Impact:**

- Records must enforce: no entity types, no handles, no tokens in record fields
- Type checker should reject these at compile time
- Records maintain true value semantics (copies are independent)


---

## Lock Policies and Shared/Tracked Memory Naming (2025-12-19)

### Status: ✅ COMPLETED

**Implementation:**
- ✅ Lock policy types created: Mutex, MultiReadLock, RejectEdit
- ✅ All implement `protocol LockPolicy` for compile-time type constraints
- ✅ Renamed `Retained<T>` → `Shared<T>` (single-threaded)
- ✅ Renamed `Tracked<Retained<T>>` → `Tracked<T>` (weak single-threaded)
- ✅ `Shared<T, P>` for multi-threaded with lock policy P
- ✅ `Tracked<T, P>` for weak multi-threaded references

**Lock Policy Types:**

1. **Mutex** - Exclusive locking
   - File: `stdlib/memory/controller/Mutex.rf`
   - Fields: `locked: Atomic<bool>`
   - Performance: ~15-30ns lock acquisition
   - Use case: Write-heavy workloads

2. **MultiReadLock** - Read-write lock
   - File: `stdlib/memory/controller/MultiReadLock.rf`
   - Fields: `readers: Atomic<s32>`, `writer: Atomic<bool>`
   - Performance: ~10-20ns read, ~20-35ns write
   - Use case: Read-heavy workloads (>80% reads)

3. **RejectEdit** - Immutable
   - File: `stdlib/memory/controller/RejectEdit.rf`
   - Fields: None (empty)
   - Performance: 0ns - no locking
   - Use case: Immutable configuration data

**Generic Factory Method:**
- Changed from `share(policy: P)` to `share<P>()`
- Allows compiler to specialize for each policy type
- Usage: `counter.share<Mutex>()`, `config.share<MultiReadLock>()`

**Compiler Support Required:**
- ✅ Parse `protocol LockPolicy { pass }` marker protocol
- ✅ Type check `Shared<T, P> where P follows LockPolicy`
- ✅ Validate only Mutex/MultiReadLock/RejectEdit are used as P
- ✅ Support generic method syntax: `share<P>() -> Shared<T, P>`
- ✅ Specialize code generation for each lock policy
- ❌ TODO: Infer optimal lock policy based on usage patterns
- ❌ TODO: Warn when using Mutex for read-heavy workloads
- ❌ TODO: Suggest RejectEdit for immutable data

**Memory Controllers:**
- ✅ Renamed `RetainController` → `SingleShareController`
- ✅ Created `MultiShareController` with atomic operations
- ✅ Both track strong/weak counts for reference counting

**Design Philosophy:**
- **Protocol as marker** - Zero runtime cost, purely compile-time
- **Snatched<P>** - Type-preserving pointer for lock storage
- **Direct fields** - Lock types have fields directly (no "inner" wrapper)
- **Uniform layout** - All `Shared<T, *>` have same memory size

**Files:**
- `stdlib/memory/wrapper/Shared.Single.rf`
- `stdlib/memory/wrapper/Shared.Multi.rf`
- `stdlib/memory/wrapper/Tracked.Single.rf`
- `stdlib/memory/wrapper/Tracked.Multi.rf`
- `stdlib/memory/controller/SingleShareController.rf`
- `stdlib/memory/controller/MultiShareController.rf`
- `stdlib/memory/controller/Mutex.rf`
- `stdlib/memory/controller/MultiReadLock.rf`
- `stdlib/memory/controller/RejectEdit.rf`

**See Also:**
- [RazorForge Lock Policies](../wiki/RazorForge-Lock-Policies.md) - Complete documentation
- [RazorForge Concurrency Model](../wiki/RazorForge-Concurrency-Model.md) - Concurrency overview
- [RazorForge Handles vs Tokens](../wiki/RazorForge-Handles-vs-Tokens.md) - Memory model

---


