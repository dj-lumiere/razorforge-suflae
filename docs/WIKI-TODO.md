# RazorForge Wiki Documentation TODO

**Last Updated:** 2025-12-17

This document tracks documentation tasks for the RazorForge and Suflae wiki. For compiler implementation tasks, see [COMPILER-TODO.md](COMPILER-TODO.md). For LSP tasks, see [LSP-TODO.md](LSP-TODO.md).

---

## HIGH PRIORITY

*No high-priority tasks remaining.*

---

## MEDIUM PRIORITY

### Missing Feature Documentation

**Status:** ⏸️ **TODO** - Features exist but not documented

1. **Generic Constraints:** ✅ **COMPLETE** (2025-12-17)
    - Already documented in `wiki/Generics.md`
    - Covers `<T follows Protocol>` syntax
    - Examples with `Comparable`, `Hashable`, etc.
    - Multiple constraints: `<T follows A, B>`
    - Combined constraints: `where T from Base follows Protocol`

2. **Protocol Mixins:** ✅ **COMPLETE** (2025-12-17)
    - Updated `Comparable` protocol to use three-way comparison (`__cmp__`)
    - Compiler auto-generates `__lt__`, `__le__`, `__gt__`, `__ge__` from `__cmp__`
    - Uses `ComparisonSign` choice (LEFT_SMALL, EQUAL, RIGHT_SMALL)
    - Updated in:
      - `wiki/RazorForge-Protocols.md`
      - `wiki/Suflae-Protocols.md`
      - `wiki/RazorForge-Operator-Overloading.md`
      - `wiki/Suflae-Operator-Overloading.md`
    - Protocol fields (mixin pattern) already documented with `Cached<T>` example

3. **Attribute System:**
    - Comprehensive list of all attributes
    - Usage examples for each
    - Compiler vs user-defined attributes

4. **Memory Model Deep Dive:**
    - Iterator permission inference (R/RW/RWM)
    - Migratable operations
    - When buffers can relocate

5. **Error Handling Patterns:**
    - Best practices for `!` functions
    - When to use `try_` vs `check_` vs `find_`
    - Error type design guidelines

---

## LOW PRIORITY

### Examples and Tutorials

**Status:** ⏸️ **TODO** - Need practical examples

1. **Complete Project Examples:**
    - Simple CLI tool
    - REST API server
    - Game with entity-component system
    - Data processing pipeline

2. **Common Patterns:**
    - Builder pattern with records and `with`
    - State machines with choices
    - Actor pattern with `Shared<T>`
    - Error handling strategies

3. **Performance Optimization:**
    - When to use records vs entities
    - Memory layout considerations
    - Token usage patterns for zero-copy

---

## COMPLETED

### Error Handling Documentation Fixes (2025-12-17)
✅ **COMPLETE** - Fixed critical misconceptions about Maybe/Result/Lookup

**Problem 1**: Documentation showed manual implementations of `check_`, `try_`, `find_` functions, which users should NEVER write.

**Fix**: Updated all examples to show correct pattern:
- ✅ Users write `!` functions with `throw`/`absent`
- ✅ Compiler automatically generates safe variants:
  - `throw` only → generates `check_` (Result) + `try_` (Maybe)
  - `absent` only → generates `try_` (Maybe)
  - `throw` + `absent` → generates `find_` (Lookup) + `try_` (Maybe)
- ❌ Users NEVER manually write functions returning Maybe/Result/Lookup

**Problem 2**: Documentation showed incorrect Result<T> and Lookup<T> operations (`.or()`, `.select()`, `.is_valid()`, etc.).

**Fix**: Clarified Result<T> and Lookup<T> have **NO API** - can ONLY be dismantled via language constructs:
- ✅ Result<T> supports ONLY: `??` operator, `when` pattern matching, `if is` type narrowing
- ❌ Result<T> has NO methods: no `.or()`, `.select()`, `.is_valid()`, or any other methods
- ✅ Lookup<T> supports ONLY: `??` operator (ignores both error and absent), `when` pattern matching, `if is` type narrowing
- ❌ Lookup<T> has NO methods: no `.or()`, `.select()`, `.is_valid()`, `.is_none()`, `.is_error()`, `.to_maybe()`, `.to_result()`, or any other methods
- **Key principle**: These types must be dismantled/filtered before the inner value can be used

**Problem 3**: Documentation didn't explain Result<T>/Lookup<T> "use immediately" semantics or `if is` type narrowing.

**Fix**: Added comprehensive error handling type system documentation:
- ✅ Result<T>/Lookup<T> are compiler-provided opaque types (NOT normal variants)
- ✅ "Use immediately" semantics: can store in locals, cannot pass to functions or return from user functions
- ✅ Variant is more permissive: can pass to functions, return, store in collections
- ✅ Three dismantling constructs: `??` operator, `when` pattern matching, `if is` type narrowing
- ✅ `if is` early return pattern for clean control flow
- ✅ Type narrowing: `if result is Crashable e { return }` → result is auto-unwrapped to T after check
- ✅ Sequential narrowing for Lookup<T>: check error, then check None, then use unwrapped value

**Files Updated**:
- `RazorForge-Error-Handling.md` - Fixed 5+ manual implementations + removed incorrect operations + added if is patterns
- `Suflae-Error-Handling.md` - Fixed manual check_ implementations + added if is patterns
- `COMPILER-TODO.md` - Added task #5 for Result/Lookup opaque types and type narrowing
- `LSP-TODO.md` - Added Phase 3.5 for error handling type system support

**Key Clarifications Added**:
- Generation pattern depends on function body (`throw` vs `absent` vs both)
- Compiler-generated functions are clearly marked
- Added "❌ WRONG" examples showing what NOT to do
- Result<T> supports ONLY `??` and `when` (nothing else)

### Crashable Types Design Resolution (2025-12-17)
✅ **COMPLETE** - Resolved design question #2: Crashable types are entities

**Decision**: Use entity for all Crashable error types (not records)

**Rationale**:
- `Result<T>`, `Lookup<T>`, and `Variant` are records that hold `Snatched` handles internally
- Errors are heap-allocated anyway when thrown or returned
- `ValueText<letter32, 256>` = 1KiB per field (too large, init-only)
- Stack traces require heap allocation (`List<StackFrame>`)
- Entity provides better ergonomics: dynamic `Text`, string interpolation
- Errors are exceptional, not performance-critical hot path

**Files Updated**:
- **design-todo.md** - Marked #2 and #3 as resolved
  - #2: Crashable types → entity
  - #3: No ValueText for Suflae (keeps language simpler)

- **RazorForge-Error-Handling.md** - Updated 9 error examples:
  - Changed all `record XError` to `entity XError`
  - Changed `ValueText<256>` to `Text<letter32>`
  - Added Crashable protocol fields (message_handle, stack_trace, etc.)
  - Moved crash_message() outside type definition with `@readonly`

- **RazorForge-Protocols.md**:
  - Updated ValidationError example to entity

- **Suflae-Error-Handling.md** - Updated 7 error examples:
  - Changed all `record XError` to `entity XError`
  - Errors use dynamic `Text` (no ValueText in Suflae)

- **Suflae-Protocols.md**:
  - Updated note explaining entity-based errors
  - Removed reference to "ValueText not yet added"
  - Clarified design decision

### Operator Overloading & Protocol Documentation (2025-12-17)
✅ **COMPLETE** - Comprehensive operator overloading and protocol documentation
- **Created `wiki/RazorForge-Operator-Overloading.md`** - 830+ line comprehensive guide covering:
  - Type capability matrix (which types can overload which operators)
  - Equality operators (`__eq__`, `__ne__`)
  - **Comparable protocol using three-way comparison (`__cmp__` -> `ComparisonSign`)**
  - **Three-way comparison operator (`<=>`) that calls `__cmp__`**
  - Compiler auto-generates `__lt__`, `__le__`, `__gt__`, `__ge__` from `__cmp__`
  - `ComparisonSign` choice: `LEFT_SMALL`, `EQUAL`, `RIGHT_SMALL`
  - Arithmetic operators (`__add__`, `__sub__`, `__mul__`, etc.)
  - Text operators (`+` concatenation, `*` repetition)
  - Bitwise operators (`__and__`, `__or__`, `__xor__`, `__not__`)
  - Shift operators (arithmetic/logical left/right)
  - Index operators (`__getitem__`, `__setitem__`)
  - Clarified unwrap operator (`??`) is NOT overloadable (built-in for Maybe/Result/Lookup only)
  - Updated container examples with `public private(set) var count: uaddr`
  - Best practices and common patterns
  - Proper syntax: no trailing commas in record/entity fields, `routine Me.__method__` in protocols

- **Created `wiki/Suflae-Operator-Overloading.md`** - 750+ line comprehensive guide with Python-style syntax

- **Updated `wiki/RazorForge-Protocols.md`:**
  - Updated Comparable protocol to use `__cmp__` pattern
  - Fixed all `ValueText` instances to include letter type parameter (e.g., `ValueText<letter32, 128>`)

- **Updated `wiki/Suflae-Protocols.md`:**
  - Updated Comparable protocol to use `__cmp__` pattern
  - Corrected ValidationError example (changed from invalid `record` with `Text` fields to `entity`)
  - Added note referencing design-todo.md #3 about ValueText for Suflae

- **Fixed `wiki/Operators.md`:**
  - Changed equality operators from "non-overloadable" to "overloadable" (via `__eq__`/`__ne__`)
  - Added Text operators section (concatenation and repetition)
  - Updated See Also section with links to operator overloading guides
  - Fixed summary table to show equality as overloadable

### Record Documentation Comprehensive Update (2025-12-17)
✅ **COMPLETE** - See COMPILER-TODO.md for full changelog
- Lens syntax clarification
- With expression syntax fixes
- Record content restrictions
- Terminology updates
- Suflae-specific corrections

---

## Related Documentation

**Implementation:**
- [COMPILER-TODO.md](COMPILER-TODO.md) - Compiler implementation tasks
- [LSP-TODO.md](LSP-TODO.md) - LSP implementation tasks
- [STDLIB-TODO.md](STDLIB-TODO.md) - Standard library tasks

**Language Design:**
- [RazorForge Memory Model](../wiki/RazorForge-Memory-Model.md)
- [RazorForge Variants](../wiki/RazorForge-Variants.md)
- [RazorForge Handles vs Tokens](../wiki/RazorForge-Handles-vs-Tokens.md)
