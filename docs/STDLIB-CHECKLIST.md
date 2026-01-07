# RazorForge Standard Library Checklist

This document tracks the implementation status of all standard library components.

## Legend

- ✅ Complete - All operators/methods implemented and tested
- ⏳ Partial - Some functionality implemented
- ❌ Not Started - Stub only or missing
- 🔧 Needs Review - Implementation exists but may need updates

---

## Native Data Types (`stdlib/NativeDataTypes/`)

### Signed Integers

| Type | Arithmetic   | Overflow Variants | Bitwise    | Shifts              | Comparison        | Conversions         | Status |
|------|--------------|-------------------|------------|---------------------|-------------------|---------------------|--------|
| s8   | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| s16  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| s32  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| s64  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| s128 | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |

**Power Operators for Signed Types:**

- ✅ `**` - Base power (throws `IndeterminateResultError` for 0**0, `NegativeExponentError` for negative exp)
- ✅ `**%` - Wrapping power (same error checks)
- ✅ `**^` - Saturating power (same error checks)
- ✅ `**?` - Checked power (returns `None` for 0 base or negative exp)

### Unsigned Integers

| Type | Arithmetic   | Overflow Variants | Bitwise    | Shifts              | Comparison        | Conversions         | Status |
|------|--------------|-------------------|------------|---------------------|-------------------|---------------------|--------|
| u8   | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| u16  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| u32  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| u64  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| u128 | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |

**Power Operators for Unsigned Types:**

- ✅ `**` - Base power (throws `IndeterminateResultError` for 0**0, no negative exp check)
- ✅ `**%` - Wrapping power (same error checks)
- ✅ `**^` - Saturating power (same error checks)
- ✅ `**?` - Checked power (returns `None` for 0 base)

### Address Types (Platform-Dependent)

| Type  | Wrapping Arithmetic | Checked Arithmetic | Bitwise    | Shifts                   | Comparison | Status |
|-------|---------------------|--------------------|------------|--------------------------|------------|--------|
| saddr | ✅ +%,-%, *%         | ✅ +?,-?,*?,//?,%   | ✅ &,\|,^,~ | ✅ <<?,>>,<<<,>>> (no <<) | ✅ all      | ✅      |
| uaddr | ✅ +%,-%, *%         | ✅ +?,-?,*?,//?,%   | ✅ &,\|,^,~ | ✅ <<?,<<<,>>> (no >>)    | ✅ all      | ✅      |

**Note:** Address types have NO base `+`, `-`, `*` operators - only wrapping/checked variants.

### Floating Point Types

| Type | Basic Arithmetic | Math Functions | Comparison | Conversions | Status |
|------|------------------|----------------|------------|-------------|--------|
| f16  | ⏳ +,-,*,/        | ❌              | ⏳          | ⏳           | ⏳      |
| f32  | ⏳ +,-,*,/        | ❌              | ⏳          | ⏳           | ⏳      |
| f64  | ⏳ +,-,*,/        | ❌              | ⏳          | ⏳           | ⏳      |
| f128 | ⏳ +,-,*,/        | ❌              | ⏳          | ⏳           | ⏳      |

### Decimal Floating Point Types

| Type | Basic Arithmetic | Math Functions | Comparison | Conversions | Status |
|------|------------------|----------------|------------|-------------|--------|
| d32  | ❌                | ❌              | ❌          | ❌           | ❌      |
| d64  | ❌                | ❌              | ❌          | ❌           | ❌      |
| d128 | ❌                | ❌              | ❌          | ❌           | ❌      |

### Other Native Types

| Type  | Implementation                   | Status |
|-------|----------------------------------|--------|
| bool  | ✅ Logical operators, conversions | ✅      |
| Blank | ✅ Unit type                      | ✅      |
| None  | ✅ None type for Maybe            | ✅      |

---

## Error Types (`stdlib/errors/`)

| Type                     | Description                   | Status |
|--------------------------|-------------------------------|--------|
| Error                    | Base error type               | ✅      |
| Crashable                | Base for crash-causing errors | ✅      |
| DivisionByZeroError      | Division by zero              | ✅      |
| IntegerOverflowError     | Integer overflow              | ✅      |
| IndeterminateResultError | 0**0 case                     | ✅      |
| NegativeExponentError    | Negative exponent on integers | ✅      |
| IndexOutOfBoundsError    | Array/list index error        | ✅      |
| stackframe               | Stack frame info              | ⏳      |
| stacktrace               | Stack trace collection        | ⏳      |
| message                  | Error message handling        | ⏳      |
| common                   | Common error utilities        | ⏳      |

---

## Error Handling Types (`stdlib/ErrorHandling/`)

| Type      | Description                    | Status |
|-----------|--------------------------------|--------|
| Maybe<T>  | Optional value (value or None) | ✅      |
| Result<T> | Value or Error                 | ✅      |
| Lookup<T> | Value, None, or Error          | ✅      |

---

## Collections (`stdlib/Collections/`)

### Lists

| Type            | Type Kind | Core Operations                             | Iteration | Status |
|-----------------|-----------|---------------------------------------------|-----------|--------|
| List<T>         | entity    | ✅ push, pop, get, set, insert, remove, len  | ✅         | ✅      |
| FixedList<T, N> | resident  | ✅ push, pop, get, set, insert, remove       | ✅         | ✅      |
| ValueList<T, N> | record    | ✅ get only (immutable, use `with` for mods) | ✅         | ✅      |

### Deques (Double-Ended Queues)

| Type             | Type Kind | Core Operations                        | Iteration | Status |
|------------------|-----------|----------------------------------------|-----------|--------|
| Deque<T>         | entity    | ✅ push/pop front+back, rotate, reverse | ✅         | ✅      |
| FixedDeque<T, N> | resident  | ✅ push/pop front+back, rotate, reverse | ✅         | ✅      |

### Sets

| Type           | Type Kind | Strategy           | Core Operations                                  | Iteration | Status |
|----------------|-----------|--------------------|--------------------------------------------------|-----------|--------|
| Set<T>         | entity    | Separate chaining  | ✅ insert, remove, contains, set ops              | ✅         | ✅      |
| FixedSet<T, N> | resident  | Robin hood hashing | ✅ insert, remove, contains                       | ✅         | ✅      |
| SortedSet<T>   | entity    | B-tree             | ✅ insert, remove, contains, range, `__getitem__` | ✅         | ✅      |

### Dictionaries

| Type               | Type Kind | Strategy           | Core Operations                             | Iteration | Status |
|--------------------|-----------|--------------------|---------------------------------------------|-----------|--------|
| Dict<K, V>         | entity    | Separate chaining  | ✅ insert, remove, get, keys/values          | ✅         | ✅      |
| FixedDict<K, V, N> | resident  | Robin hood hashing | ✅ insert, remove, get, keys/values          | ✅         | ✅      |
| SortedDict<K, V>   | entity    | B-tree             | ✅ insert, remove, get, range, `__getitem__` | ✅         | ✅      |

### Sorted Collections

| Type          | Type Kind | Strategy | Core Operations                                  | Iteration | Status |
|---------------|-----------|----------|--------------------------------------------------|-----------|--------|
| SortedList<T> | entity    | B-tree   | ✅ insert, remove, contains, range, `__getitem__` | ✅         | ✅      |

### Priority Queues

| Type                               | Type Kind | Strategy    | Core Operations   | Iteration      | Status |
|------------------------------------|-----------|-------------|-------------------|----------------|--------|
| PriorityQueue<TElement, TPriority> | entity    | Binary heap | ✅ push, pop, peek | ✅ (heap order) | ✅      |

### Bit Collections

| Type            | Type Kind | Core Operations                               | Iteration | Status |
|-----------------|-----------|-----------------------------------------------|-----------|--------|
| BitList         | entity    | ✅ push, pop, get, set, count_ones/zeros, flip | ✅         | ✅      |
| FixedBitList<N> | resident  | ✅ push, pop, get, set, count_ones/zeros, flip | ✅         | ✅      |
| ValueBitList<N> | record    | ✅ get only (immutable, use `with` for mods)   | ✅         | ✅      |

### Tuples

| Type       | Type Kind | Core Operations | Iteration | Status |
|------------|-----------|-----------------|-----------|--------|
| Tuple      | entity    | ⏳               | ❌         | ⏳      |
| ValueTuple | record    | ⏳               | ❌         | ⏳      |

### Collection Implementation Notes

**Type Kinds:**

- `entity` - Heap-allocated, mutable, dynamic growth
- `resident` - Fixed-size at compile time, reference semantics, internal mutability
- `record` - Value type, immutable, use `with` statement for modifications

**Hashing Strategies:**

- **Separate chaining**: Uses SortedSet/SortedDict as bucket chains - O(log k) per chain
- **Robin hood hashing**: Open addressing with probe distance tracking - cache-friendly, O(1) average
- **B-tree**: Balanced tree with O(log n) operations, supports range queries and indexed access

---

## Text Types (`stdlib/Text/`)

**UTF-32 Text (default):**

| Type               | Core Operations | Unicode | Iteration | Status |
|--------------------|-----------------|---------|-----------|--------|
| Text               | ⏳               | ⏳       | ⏳         | ⏳      |
| TextBuffer         | ⏳               | ⏳       | ⏳         | ⏳      |
| FixedText<N>       | ⏳               | ⏳       | ⏳         | ⏳      |
| FixedTextBuffer<N> | ⏳               | ⏳       | ⏳         | ⏳      |
| ValueText<N>       | ⏳               | ⏳       | ⏳         | ⏳      |

**UTF-8 Bytes:**

| Type                | Core Operations | Unicode | Iteration | Status |
|---------------------|-----------------|---------|-----------|--------|
| Bytes               | ⏳               | ⏳       | ⏳         | ⏳      |
| BytesBuffer         | ⏳               | ⏳       | ⏳         | ⏳      |
| FixedBytes<N>       | ⏳               | ⏳       | ⏳         | ⏳      |
| FixedBytesBuffer<N> | ⏳               | ⏳       | ⏳         | ⏳      |
| ValueBytes<N>       | ⏳               | ⏳       | ⏳         | ⏳      |

**Character Types:**

| Type   | Description              | Status |
|--------|--------------------------|--------|
| letter | 32-bit Unicode codepoint | ⏳      |
| byte   | 8-bit UTF-8 code unit    | ⏳      |

**Note:** `letter8`, `letter16`, `letter32` were **RENAMED/REMOVED**:

- `letter32` → `letter` (32-bit Unicode codepoint)
- `letter8` → `byte` (8-bit UTF-8 code unit)
- `letter16` → **REMOVED** (UTF-16 handled via conversion methods)

---

## Memory Types (`stdlib/memory/`)

### Core Memory Types

| Type            | Description            | Status |
|-----------------|------------------------|--------|
| DynamicSlice<T> | Dynamic memory slice   | ⏳      |
| MemorySize      | Memory size with units | ✅      |

**Note:** `TemporarySlice<T>` was **REMOVED** - stack-allocated slices are no longer supported.

### Memory Wrappers (`stdlib/memory/wrapper/`)

**Tokens (non-storable, temporary access):**

| Type           | Description                                | Status |
|----------------|--------------------------------------------|--------|
| `Viewed<T>`    | Read-only access (single-threaded)         | ⏳      |
| `Hijacked<T>`  | Exclusive mutable access (single-threaded) | ⏳      |
| `Inspected<T>` | Read lock (multi-threaded)                 | ⏳      |
| `Seized<T>`    | Write lock (multi-threaded)                | ⏳      |

**Handles (storable, owning):**

| Type            | Description                                         | Status |
|-----------------|-----------------------------------------------------|--------|
| `Shared<T>`     | Reference-counted (single-threaded)                 | ⏳      |
| `Shared<T, P>`  | Reference-counted with lock policy (multi-threaded) | ⏳      |
| `Tracked<T>`    | Weak reference to `Shared<T>`                       | ⏳      |
| `Tracked<T, P>` | Weak reference to `Shared<T, P>`                    | ⏳      |
| `Snatched<T>`   | Raw pointer (danger zone only)                      | ⏳      |

**Note:** `Retained<T>` was **RENAMED** to `Shared<T>` (single-threaded reference counting).

### Memory Controllers (`stdlib/memory/controller/`)

| Type                    | Description                           | Status |
|-------------------------|---------------------------------------|--------|
| `SingleShareController` | Reference counting for `Shared<T>`    | ⏳      |
| `MultiShareController`  | Reference counting for `Shared<T, P>` | ⏳      |

### Lock Policies (`stdlib/memory/controller/`)

| Type          | Description                  | Status |
|---------------|------------------------------|--------|
| LockPolicy    | Protocol for lock policies   | ⏳      |
| Mutex         | Exclusive locking (~15-30ns) | ⏳      |
| MultiReadLock | Read-write lock (~10-35ns)   | ⏳      |
| RejectEdit    | Immutable (zero overhead)    | ⏳      |

---

## Arbitrary Precision Types (Suflae)

| Type     | Basic Arithmetic | Advanced Operations | Status |
|----------|------------------|---------------------|--------|
| Integer  | ⏳ +,-,*,//,%     | ⏳                   | ⏳      |
| Fraction | ⏳ +,-,*,/        | ⏳                   | ⏳      |
| Decimal  | ⏳ +,-,*,/        | ⏳                   | ⏳      |

---

## Console I/O (`stdlib/Console.rf`)

| Function                        | Description                         | Status |
|---------------------------------|-------------------------------------|--------|
| show<T>(value)                  | Print value                         | ✅      |
| get_letters(prompt) -> Text     | Read individual letters             | ⏳      |
| get_word(prompt) -> Text        | Read single word (whitespace delim) | ⏳      |
| get_line(prompt) -> Text        | Read single line                    | ⏳      |
| get_words(prompt) -> List<Text> | Read multiple words                 | ⏳      |
| get_lines(prompt) -> List<Text> | Read multiple lines                 | ⏳      |
| get_all(prompt) -> Text         | Read all input until EOF            | ⏳      |

---

## Atomic Types (`stdlib/AtomicDataTypes/`)

| Type           | Description         | Status |
|----------------|---------------------|--------|
| `Atomic<Bool>` | Thread-safe boolean | ⏳      |

---

## Runtime (`stdlib/Runtime/`)

| Component         | Description         | Status |
|-------------------|---------------------|--------|
| `CompilerService` | Compiler intrinsics | ⏳      |

---

## Core (`stdlib/core.rf`)

| Component             | Description              | Status |
|-----------------------|--------------------------|--------|
| Core type definitions | Base types and utilities | ⏳      |

---

**Note:** `CSubsystem.rf` was **REMOVED** - FFI bindings moved to respective files.

---

## Operator Implementation Checklist

### Per-Type Operator Requirements

#### Signed Integers (sN)

- [x] `+`, `+%`, `+^`, `+?` - Addition variants
- [x] `-`, `-%`, `-^`, `-?` - Subtraction variants
- [x] `*`, `*%`, `*^`, `*?` - Multiplication variants
- [x] `//`, `//?` - Floor division
- [x] `%`, `%?` - Remainder
- [x] `**`, `**%`, `**^`, `**?` - Power (with 0**0 and negative exp checks)
- [x] `&`, `|`, `^`, `~` - Bitwise
- [x] `<<`, `<<?`, `>>`, `<<<`, `>>>` - Shifts
- [x] `==`, `!=`, `<`, `<=`, `>`, `>=` - Comparison
- [x] `-` (unary) - Negation
- [x] Type conversions to/from all numeric types

#### Unsigned Integers (uN)

- [x] `+`, `+%`, `+^`, `+?` - Addition variants
- [x] `-`, `-%`, `-^`, `-?` - Subtraction variants
- [x] `*`, `*%`, `*^`, `*?` - Multiplication variants
- [x] `//`, `//?` - Floor division
- [x] `%`, `%?` - Remainder
- [x] `**`, `**%`, `**^`, `**?` - Power (with 0**0 check, no negative exp)
- [x] `&`, `|`, `^`, `~` - Bitwise
- [x] `<<`, `<<?`, `>>`, `<<<`, `>>>` - Shifts
- [x] `==`, `!=`, `<`, `<=`, `>`, `>=` - Comparison (unsigned)
- [x] Type conversions to/from all numeric types

#### Address Types (saddr, uaddr)

- [x] `+%`, `+?` - Wrapping/checked addition (NO base `+`)
- [x] `-%`, `-?` - Wrapping/checked subtraction (NO base `-`)
- [x] `*%`, `*?` - Wrapping/checked multiplication (NO base `*`)
- [x] `//?`, `%?` - Checked division/remainder
- [x] `&`, `|`, `^`, `~` - Bitwise
- [x] Shifts (saddr: `<<?, >>, <<<, >>>`, uaddr: `<<?, <<<, >>>`)
- [x] `==`, `!=`, `<`, `<=`, `>`, `>=` - Comparison

---

## Error Types Checklist

- [x] Error (base)
- [x] Crashable (base for crash errors)
- [x] DivisionByZeroError
- [x] IntegerOverflowError
- [x] IndeterminateResultError
- [x] NegativeExponentError
- [x] IndexOutOfBoundsError
- [ ] FileNotFoundError
- [ ] PermissionDeniedError
- [ ] NetworkError
- [ ] ParseError
- [ ] ValidationError

---

## Testing Checklist

### Unit Tests Needed

- [ ] All signed integer operators (s8-s128)
- [ ] All unsigned integer operators (u8-u128)
- [ ] Address type operators (saddr, uaddr)
- [ ] Floating point operators (f16-f128)
- [ ] Decimal floating point operators (d32-d128)
- [ ] Error type creation and handling
- [ ] Collection operations
- [ ] Text operations
- [ ] Memory wrapper behavior

### Integration Tests Needed

- [ ] Cross-type conversions
- [ ] Error propagation through call stack
- [ ] Memory safety with wrappers
- [ ] Generic type instantiation
- [ ] Import/module resolution

---

## Notes

### Conventions

1. **Operator naming**: `__add__` for `+`, `__add_wrap__` for `+%`, etc.
2. **Failable functions**: Use `!` suffix, compiler generates `try_`, `check_`, `find_` variants
3. **`@crash_only`**: Prevents safe variant generation
4. **`danger!` blocks**: Required for intrinsic operations

### Priorities

1. **HIGH**: Native integer types (done), error types (done), collections (done)
2. **MEDIUM**: Floating point, text, memory wrappers
3. **LOW**: Decimal types, atomic types, advanced collections
