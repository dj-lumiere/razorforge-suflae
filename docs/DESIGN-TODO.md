# Suflae/RazorForge Design TODOs

This document tracks open design questions and decisions that need to be made.

## 1. Seized<T> and Migratable Operations

**Status:** Resolved
**Date:** 2025-12-15
**Resolution:** Exclusive tokens (Hijacked/Seized) get RWM permissions

### The Question

Should `Retained<T>`/`Seized<T>`/`Shared<T>` tokens support migratable operations (RWM permissions)?

### Resolution

**Permission model:**

- **R** = Read only
- **RW** = Read + Write (in-place data modification, including rearrangement like swap/sort)
- **RWM** = Read + Write + Migratable (operations that can relocate buffer)

**Token permissions:**

- `Viewed<T>` / `Inspected<T>` (shared read) = **R** only
- `Hijacked<T>` / `Seized<T>` (exclusive mutable):
    - **Not iterating** = **RWM** (full access including migratable ops)
    - **During iteration** = **RW** (migratable ops banned to prevent iterator invalidation)
- `Retained<T>` / `Shared<T>` direct access = **RWM** (when not wrapped in token)

### Why This Works

**Migratable vs Write:**

- **Write (W)** = Operations within existing allocation (swap, sort, element mutation)
- **Migratable (M)** = Operations that can relocate buffer (push, pop, remove, resize)

**During iteration:**

```razorforge
hijacking list as h {
    # Not iterating = RWM
    h.push(item)  # ✅ Migratable ops allowed

    # During iteration = RW only
    for item in h {
        h.swap(0, 1)  # ✅ Write ops allowed
        # h.push(x)   # ❌ Migratable ops banned (would relocate buffer, invalidating iterator)
    }

    # After iteration = RWM again
    h.push(item)  # ✅ Migratable ops allowed
}
```

### Multi-threaded Usage

```razorforge
let shared: Shared<List<T>, Mutex> = ...

# Efficient batching with explicit lock scope
seizing shared as s {
    s.push(item1)  # ✅ All in same lock scope
    s.push(item2)  # Only 1 lock acquisition
    s.push(item3)
}

# Auto-locking NOT supported (violates "explicit costs" philosophy)
# shared.push(item)  # ❌ Must use seizing for migratable ops
```

### Related Docs

- [RazorForge-Compiler-Analysis](RazorForge-Compiler-Analysis.md#migratable-modification-inference)
- [RazorForge-Containers-And-Iterators](RazorForge-Containers-And-Iterators.md)
- [RazorForge-Concurrency-Model](RazorForge-Concurrency-Model.md)
- [RazorForge-Design-Philosophy](RazorForge-Design-Philosophy.md)

---

## 2. Crashable Types: Record vs Entity

**Status:** Resolved
**Date:** 2025-12-17
**Resolution:** Use entity for Crashable types

### The Question

Should Crashable error types be `record` or `entity`?

### Current State

**RazorForge:**

- All examples in `RazorForge-Error-Handling.md` show Crashable types as **records**
- Examples use `ValueText<256>` for error messages (fixed size)
- Lines 234, 256, 731: `record FileError follows Crashable`, `record ValidationError follows Crashable`

**RazorForge-Protocols.md:**

- Line 655: `record ValidationError follows Crashable`

**Crashable Protocol Definition (RazorForge-Error-Handling.md:218-226):**

```razorforge
protocol Crashable {
    # Required fields (compiler enforces these exist)
    message_handle: MessageHandle
    stack_trace: StackTrace
    file_id: u32
    routine_id: u32
    line_no: u32
    column_no: u32
    error_code: u32

    # Required routine
    routine crash_message() -> Text<letter32>
}
```

### The Problem

**Records have strict content restrictions (RazorForge-Records.md:76-85):**

- ✅ Can contain: Numeric types, Bool, other records, ValueText<Letter, Size>
- ❌ Cannot contain: Entity types (Text, Integer, etc.), Handles, Tokens, reference types

**This means:**

1. Crashable types MUST be records (all examples show this)
2. Error messages MUST use `ValueText<Letter, Size>` (fixed size)
3. Dynamic error messages requiring `Text` entity would violate record rules

**Trade-offs:**

**Using Records (Current):**

- ✅ Lightweight, stack-allocated
- ✅ Fast to create and copy
- ✅ No heap allocation overhead
- ✅ Predictable memory footprint
- ❌ Fixed size error messages (must use ValueText with size limit)
- ❌ Cannot store dynamic error context (List, Dict, etc.)

**Using Entities (Alternative):**

- ✅ Dynamic error messages (use Text entity)
- ✅ Can store rich error context (List<Text>, Dict, etc.)
- ❌ Heap allocation overhead
- ❌ Reference counting overhead
- ❌ Less predictable memory usage
- ❌ Heavier for simple errors

### Considerations

**Performance Implications:**

- Error handling is a hot path in many applications
- Records are faster to create/destroy (stack allocation)
- Entities require heap allocation + RC management

**Ergonomics:**

- ValueText size limits (64, 128, 256, etc.) may be restrictive for complex error messages
- Dynamic Text would be more ergonomic but less predictable

**Philosophy Alignment:**

- RazorForge emphasizes "explicit costs" and predictability
- Fixed-size errors align with this philosophy
- Users who need rich error context can store entity references separately

### Open Questions

1. Is the current design (record + ValueText) sufficient for most error cases?
2. Should we allow BOTH record-based and entity-based Crashable types?
3. What's the maximum reasonable size for ValueText in errors? (256? 512? 1024?)
4. Should we provide a `DynamicError` entity type for rare cases needing rich context?

### Resolution

**Use entity for Crashable types** because:

1. **No performance benefit from records**: `Result<T>`, `Lookup<T>`, and `Variant` are records that hold `Snatched`
   handles internally. Errors are heap-allocated anyway when thrown or returned.

2. **ValueText is too limiting**:
    - `ValueText<letter32, 256>` = 1KiB per field (UTF-32)
    - Init-only, can't build messages dynamically
    - Fixed size limits are arbitrary and restrictive

3. **Stack trace requires heap allocation**: The `StackTrace` field in Crashable protocol needs `List<StackFrame>`,
   which is already heap-allocated.

4. **Better ergonomics**:
    - Use `Text` for dynamic message building
    - String interpolation works naturally: `f"Expected {min}-{max}, got {actual}"`
    - Can store rich context: `List<Text>`, `Dict<Text, Text>`

5. **Errors are exceptional**: Not the hot path. For expected errors, use `Result<T>` which handles allocation
   efficiently.

**Example**:

```razorforge
entity ValidationError follows Crashable {
    message_handle: MessageHandle
    stack_trace: StackTrace
    field_name: Text<letter32>
    reason: Text<letter32>
    # ... other Crashable fields
}

throw ValidationError(
    field_name: field,
    reason: f"Expected {min}-{max}, got {actual}"
)
```

**Action Items:**

- ✅ Update RazorForge-Error-Handling.md to use entity for all Crashable examples
- ✅ Update RazorForge-Protocols.md Crashable examples
- ✅ Update Suflae-Error-Handling.md (use entity with Text)
- ✅ Update Suflae-Protocols.md (already shows entity, just needs cleanup)
- ✅ Remove ValueText recommendation from design-todo #3

### Related Docs

- [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md)
- [RazorForge Records](../wiki/RazorForge-Records.md)
- [RazorForge Protocols](../wiki/RazorForge-Protocols.md)

---

## 3. ValueList/ValueText for Suflae

**Status:** Resolved
**Date:** 2025-12-17
**Resolution:** No ValueText for Suflae; use entity-based errors

### The Question

Should Suflae have ValueList and ValueText value types like RazorForge?

### Current State

**RazorForge:**

- Has `ValueText<Letter, Size>` for fixed-size text in records
- Records can contain ValueText but NOT Text entities
- Clear separation: ValueText (value type) vs Text (entity type)

**Suflae:**

- **INCONSISTENCY FOUND:** Suflae-Protocols.md shows error types using `Text`:
  ```suflae
  record ValidationError follows Crashable:
      field_name: Text
      reason: Text
  ```
- But records cannot contain entities!
- Suflae has no ValueText equivalent documented

### The Problem

**Suflae-Protocols.md Line 238-244 shows:**

```suflae
record ValidationError follows Crashable:
    field_name: Text
    reason: Text

    routine crash_message() -> Text:
        return f"Validation failed for {me.field_name}: {me.reason}"
```

**This is IMPOSSIBLE because:**

1. ValidationError is a `record`
2. Records cannot contain entity types (Suflae-Records.md)
3. `Text` is an entity type in Suflae
4. Therefore this code would not compile!

### Considerations

**Option 1: Add ValueText/ValueList to Suflae**

- ✅ Maintains consistency with RazorForge
- ✅ Allows records to store fixed-size text/lists
- ✅ Enables record-based Crashable types
- ✅ Performance benefits (stack allocation)
- ❌ Adds complexity to Suflae (more types to learn)
- ❌ Suflae aims for simplicity over RazorForge

**Option 2: Make Crashable types entities in Suflae**

- ✅ Simpler mental model (fewer types)
- ✅ Can use Text directly
- ✅ Aligns with Suflae's "simpler Python-like" design
- ❌ Performance overhead (heap allocation + ARC)
- ❌ Inconsistent with RazorForge design
- ❌ Error handling becomes heavier

**Option 3: Use String type (if it exists)**

- Need to check if Suflae has a primitive string type

### Open Questions

1. Does Suflae have a primitive string/text type separate from Text entity?
2. Should Suflae error types be entities instead of records?
3. How important is consistency between RazorForge and Suflae error handling?
4. What's the performance impact of entity-based errors in Suflae's GC model?

### Resolution

**Do NOT add ValueText to Suflae** because:

1. **Suflae prioritizes simplicity**: Adding ValueText adds complexity without clear benefit
2. **Entity-based errors are fine**: Since errors are heap-allocated anyway (via Snatched handles in Result/Lookup),
   there's no performance penalty
3. **Matches Python's model**: Suflae is Python-like; Python exceptions use dynamic strings
4. **Consistent with RazorForge decision**: Both languages now use entity for Crashable types

**Suflae error example**:

```suflae
entity ValidationError follows Crashable:
    message_handle: MessageHandle
    stack_trace: StackTrace
    field_name: Text
    reason: Text

throw ValidationError(
    field_name: field,
    reason: f"Expected {min}-{max}, got {actual}"
)
```

**Note**: The documentation bug in Suflae-Protocols.md showing `record ValidationError` with `Text` fields was actually
pointing toward the right direction - entity-based errors.

### Action Items

- ✅ Fix Suflae-Protocols.md: Change `record ValidationError` to `entity ValidationError` with note explaining why
- ✅ Update Suflae-Error-Handling.md to use entity for all examples
- ✅ Document that Suflae does NOT have ValueText (keeps language simpler)
- ✅ Ensure consistency across all Suflae error examples

### Related Docs

- [Suflae Protocols](../wiki/Suflae-Protocols.md) - Contains buggy examples
- [Suflae Records](../wiki/Suflae-Records.md)
- [Suflae Error Handling](../wiki/Suflae-Error-Handling.md)
- [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md) - For comparison

---

## 4. Direct Access for Single-Threaded Handles

**Status:** Resolved
**Date:** 2025-12-17
**Resolution:** Option 1 - Direct access for single-threaded handles

### The Question

Should `Retained<T>` and other single-threaded handles require `.view()`/`.hijack()` methods for access, or allow direct
field/method access?

### Resolution

**`Retained<T>` and `Tracked<T>` now allow direct access** - no `.view()`/`.hijack()` methods needed.

**Final design:**

```razorforge
# Single-threaded handles - direct access
let handle: Retained<GameState> = state.retain()
handle.score += 10                    # Direct read/write
for enemy in handle.enemies { ... }  # Direct iteration

# Multi-threaded handles - scoped access required (locks)
let shared: Shared<GameState, Mutex> = state.share(policy: Mutex)
seizing shared as s {
    s.score += 10
}
```

### Why This Decision

**Rationale:**

1. **Single-threaded handles are safe by definition** - No race conditions possible
2. **Ceremony without benefit** - `.view()`/`.hijack()` added verbosity with no safety gain
3. **Indentation explosion** - Scoped syntax created deep nesting for simple operations
4. **Iteration locking is sufficient** - During iteration, structure is locked (prevents `@migratable` operations)
5. **Consistency with owned entities** - If you own it directly, you access it directly; `Retained<T>` is just counted
   ownership

**Safety is preserved:**

- Compiler still tracks R/RW/RWM access modes automatically
- `@migratable` operations banned during iteration (structure locking)
- Multi-threaded `Shared<T, Policy>` still requires scoped syntax for locks

### The Problem (Historical Context)

**Ergonomics issues:**

1. **Indentation problem** - Scoped syntax creates deep nesting:

```razorforge
routine process() {
    let handle = get_data()

    if some_condition {
        viewing handle as v {
            for item in v.items {
                if item.valid {
                    hijacking handle as h {
                        h.counter += 1
                    }
                }
            }
        }
    }
}
# 5+ levels of indentation!
```

2. **Unnecessary ceremony** - Example showing verbosity:

```razorforge
# Current: verbose for simple operations
routine update_game(state: Retained<GameState>) {
    hijacking state as s {
        when s.current_mode {
            is MENU => {
                s.current_mode = PLAYING
                s.score = 0
            }
            is PLAYING => {
                s.score += 10
            }
            else => pass
        }
    }
}

# Could be simpler with direct access
routine update_game(state: Retained<GameState>) {
    when state.current_mode {
        is MENU => {
            state.current_mode = PLAYING
            state.score = 0
        }
        is PLAYING => {
            state.score += 10
        }
        else => pass
    }
}
```

3. **Redundant safety** - `Retained<T>` is single-threaded:
    - The type system already makes sharing explicit
    - No concurrency risk
    - `.view()`/`.hijack()` adds ceremony without safety benefit

### Considerations

**Option 1: Direct Access for Single-Threaded Handles**

**Proposal:**

- `Retained<T>`, `Tracked<T>` → **Direct access** (no `.view()`/`.hijack()`)
- `Shared<T, Mutex>`, `Shared<T, RwLock>` → **Scoped access required** (locks needed)

**Pros:**

- ✅ Simpler, more ergonomic code
- ✅ No indentation issues
- ✅ Type system already makes sharing explicit
- ✅ Safety where it matters (multi-threading locks)
- ✅ Common case (single-threaded) is easiest

**Cons:**

- ❌ Inconsistency between handle types
- ❌ Less uniform API
- ❌ Mutation tracking might be harder to analyze

**Example:**

```razorforge
# Single-threaded - direct access
let handle: Retained<GameState> = state.retain()
handle.score += 10                    # Direct read/write
for enemy in handle.enemies { ... }  # Direct iteration

# Multi-threaded - scoped access (locks)
let shared: Shared<GameState, Mutex> = state.share(policy: Mutex)
seizing shared as s {
    s.score += 10
}
```

**Option 2: Keep Current Design (Uniform API)**

**Pros:**

- ✅ Consistent API across all handle types
- ✅ Explicit access control at use site
- ✅ Clear visual distinction for mutation

**Cons:**

- ❌ Deep indentation problems
- ❌ Verbose for common single-threaded case
- ❌ Redundant ceremony when type already conveys safety

**Option 3: Hybrid - Both Inline and Scoped**

**Current approach** - provide both:

- Inline methods for quick access: `handle.view().field`, `handle.hijack().field = x`
- Scoped syntax for grouped operations: `viewing/hijacking handle as h { ... }`

**Pros:**

- ✅ Flexibility - use what fits the situation
- ✅ Inline avoids indentation for single operations
- ✅ Scoped groups multiple operations

**Cons:**

- ❌ Still verbose for inline: `handle.hijack().score = 10`
- ❌ Two ways to do the same thing
- ❌ Doesn't solve the ceremony problem

### Open Questions

1. **Is direct access for `Retained<T>` too implicit?** The type already says "retained", is that explicit enough?

2. **Iteration use case:** Should `for item in handle.collection` work directly?
    - Current: `for item in handle.view().collection`
    - Proposed: `for item in handle.collection`

3. **Method calls:** Should `handle.method()` work directly?
    - Current: `handle.hijack().method()` (if mutating) or `handle.view().method()` (if readonly)
    - Proposed: `handle.method()` (compiler determines R/RW/RWM)

4. **Consistency philosophy:** Is it better to have uniform APIs (all handles use scoped syntax) or ergonomic APIs (
   simple cases are simple)?

5. **Migration path:** If we change this, what's the impact on existing code/examples?

### Possible Resolution Path

**Suggested approach:**

1. **Single-threaded handles (`Retained<T>`, `Tracked<T>`)** → Direct access
    - Type makes sharing explicit
    - No locks needed
    - Clean, ergonomic

2. **Multi-threaded handles (`Shared<T, Policy>`)** → Scoped access required
    - Locks must be explicit
    - Safety enforced by scope
    - Lock lifetime is clear

3. **Remove `.view()`/`.hijack()` inline methods** from single-threaded handles
    - Only keep scoped `viewing`/`hijacking` for multi-operation batching (optional, rarely needed)

4. **Compiler analysis** determines R/RW/RWM automatically based on usage

### Related Docs

- [RazorForge Memory Model](../wiki/RazorForge-Memory-Model.md)
- [RazorForge Handles vs Tokens](../wiki/RazorForge-Handles-vs-Tokens.md)
- [RazorForge Concurrency Model](../wiki/RazorForge-Concurrency-Model.md)

---

## 5. Protocol Field Requirements

**Status:** Resolved
**Date:** 2025-12-17
**Resolution:** Protocols only define method signatures, not fields

### The Question

Should protocols be able to specify required fields, or only method signatures?

### Resolution

**Protocols only define method signatures. Implementations use extension methods.**

```razorforge
# Crashable protocol - ONE method only
protocol Crashable {
    @readonly
    routine Me.message() -> Text<letter32>
}
```

**Implementations have fields + extension method:**

```razorforge
# Custom error type - just your data
entity FileError follows Crashable {
    path: Text<letter32>
    reason: Text<letter32>
}

# Extension method implementing protocol
@readonly
routine FileError.message() -> Text<letter32> {
    return f"File error at '{me.path}': {me.reason}"
}

# Using throw
routine open_file!(path: Text) -> File {
    unless File.exists(path) {
        throw FileError(path: path, reason: "Not found")
        # Compiler at 'throw' site automatically:
        # - Captures stack trace
        # - Records file/line/column location
        # - Calls console.alert() with error.message() + debug info
    }
    return File.open(path)
}
```

### Why This Decision

**Rationale:**

1. **Traditional separation of concerns** - Protocols define behavioral contracts, not data layout
2. **Implementation flexibility** - Types can store data however they want
3. **Extension methods** - Clean separation of interface (protocol) from implementation
4. **`throw` does the work** - Stack traces, location, debug info all generated at throw site
5. **Consistency** - Same pattern as Java, C#, Rust, Go, and most mainstream languages
6. **No computed properties** - Without computed properties, protocol field requirements would just be ceremony

**Key benefit:** Error types are just plain entities with your data. The protocol requires ONE method (`.message()`).
The `throw` keyword generates all debug machinery automatically.

### The Problem (Historical Context)

**Is this weird?** Most languages only allow protocols/interfaces to specify method signatures:

**Traditional approach (Java, C#, Rust traits):**

```razorforge
protocol Crashable {
    routine crash_message() -> Text
    routine get_error_code() -> u32
    routine get_file_id() -> u32
    # etc - only methods, no fields
}
```

**Current approach (Swift-like properties):**

```razorforge
protocol Crashable {
    message_handle: MessageHandle  # Field requirement
    error_code: u32                # Field requirement

    routine crash_message() -> Text
}
```

### Considerations

**Arguments FOR protocol fields:**

✅ **More expressive data contracts:**

- Can require specific data layout
- Useful for protocols that need direct field access
- Avoids getter/setter boilerplate

✅ **Performance:**

- Direct field access is faster than method calls
- No indirection through getters
- Better for hot paths

✅ **Natural for some domains:**

- Error types need standard fields (stack trace, message, etc.)
- Data-oriented protocols (Position, Size, Color, etc.)
- Interop with C/FFI (struct layout matters)

✅ **Some precedent:**

- Swift has protocol properties
- Some languages allow this pattern

**Arguments AGAINST protocol fields:**

❌ **Unusual pattern:**

- Most languages only specify behavior (methods), not structure (fields)
- Might confuse users coming from Java/C#/Rust

❌ **Blurs abstraction boundary:**

- Protocols should define "what" (interface), not "how" (implementation)
- Fields expose implementation details
- Less flexible - implementing types must match exact structure

❌ **Alternative exists:**

- Can achieve same with required getter/setter methods
- More flexible - implementation can be computed or derived
- Example: `routine get_error_code() -> u32` instead of `error_code: u32`

❌ **Versioning concerns:**

- Changing field types is breaking
- Getters allow adapting return types more easily

### Use Cases

**Where field requirements make sense:**

1. **Crashable protocol** - Standard error fields needed by runtime
2. **FFI/interop** - C structs need exact field layout
3. **Performance-critical** - Direct field access for hot paths
4. **Data DTOs** - Simple data transfer objects

**Where methods would be better:**

1. **Abstract behavior** - "How to do X" not "What data you have"
2. **Computed properties** - Value derived from other data
3. **Backwards compatibility** - Can change implementation without breaking protocol

### Open Questions

1. **Is the Crashable use case compelling enough** to justify protocol fields?

2. **Should we have both?**
    - Field requirements for data-oriented protocols
    - Method-only protocols for behavior-oriented abstractions

3. **Syntax distinction?**
    - Should field requirements look different from method signatures?
    - Current: both use same syntax, might be confusing

4. **Implementation flexibility:**
    - Should implementing type be able to use getter/setter instead of real field?
    - Or must it be an actual field with exact layout?

5. **Rust-like associated types:**
    - Could we use associated types instead: `type ErrorCode: u32`?
    - More flexible but more complex

### Possible Resolutions

**Option 1: Keep current design** - Allow field requirements

- Natural for Crashable use case
- Performance benefits
- Less boilerplate
- Document when to use fields vs methods

**Option 2: Methods only** - Remove field requirements

- More traditional design
- More flexible
- Replace with getters: `routine error_code() -> u32`
- More verbose but more familiar

**Option 3: Distinguish data vs behavior protocols**

- `data protocol` - can have fields
- `protocol` - methods only
- Clear intent in declaration

**Option 4: Auto-generate getters**

- Field requirements auto-generate getter methods
- Best of both worlds?
- More magic, might be confusing

### Related Docs

- [RazorForge Protocols](../wiki/RazorForge-Protocols.md)
- [Suflae Protocols](../wiki/Suflae-Protocols.md)
- [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md)

---

## 6. Generator Routines (`generate` keyword)

**Status:** Open
**Date:** 2025-12-30

### The Question

How should generator routines work in RazorForge and Suflae?

### Current Design Decision

**Implicit conversion**: Any routine using `generate` becomes a generator. Return type must be `Iterator<T>`.

```razorforge
routine fibonacci() -> Iterator<S32> {
    let a = 0
    let b = 1
    loop {
        generate a  // yield value
        (a, b) = (b, a + b)
    }
}

# Usage
for num in fibonacci() {
    if num > 100 { break }
    show(num)
}
```

### Open Questions

| Issue                     | Question                                                                 | Possible Solutions                                                  |
|---------------------------|--------------------------------------------------------------------------|---------------------------------------------------------------------|
| **Return type mismatch**  | What if `generate` is used but return type is `S32` not `Iterator<S32>`? | Compile error: "routine using `generate` must return `Iterator<T>`" |
| **Mixed return/generate** | Can you `return` early from a generator?                                 | Allow it (ends iteration, like Python) or forbid it?                |
| **Nested lambdas**        | Does `generate` inside a lambda yield from outer generator?              | Probably compile error - `generate` only valid at routine level     |
| **State storage**         | Generator must preserve state between `.next()` calls. Where?            | Heap-allocated coroutine frame (has memory cost)                    |
| **Error handling**        | If generator throws mid-iteration, what happens?                         | Iteration stops, error propagates to caller                         |

### Alternatives Considered

**Option A: Implicit (CHOSEN)** - Using `generate` in a routine with `Iterator<T>` return type makes it a generator.

**Option B: Explicit modifier** - Use `generator routine` modifier, return type is just `T`.

```razorforge
generator routine fibonacci() -> S32 {
    generate 1
    generate 1
    # ...
}
```

**Option C: Special return type** - `Generator<T>` instead of `Iterator<T>`.

### Why Option A

- Simpler syntax, no new modifier needed
- Return type already indicates intent
- Consistent with how `suspended` works (modifier on routine, but caller knows from return type)

### Related Docs

- [RazorForge Routines](../wiki/RazorForge-Routines.md)
- [Suflae Routines](../wiki/Suflae-Routines.md)
- [Keyword Comparison](../wiki/Keyword-Comparison.md) - `generate` = `yield`

---

## 7. Async Routines and Task Spawning

**Status:** Open
**Date:** 2025-12-30

### The Question

How should async routines (`suspended`), awaiting (`waitfor`), and task spawning (`run_as_task`) work?

### Current Design Decisions

**Keywords:**

- `suspended` - Marks a routine as async
- `waitfor` - Awaits an async result
- `run_as_task` - Fire-and-forget task spawning (stdlib function, not keyword)

```razorforge
suspended routine fetch_data(url: Text) -> Text {
    let response = waitfor http.get(url)
    return response.body
}

suspended routine start() {
    # Fire-and-forget - runs in background
    run_as_task(fetch_data("https://example.com"))

    # Wait for result
    let data = waitfor fetch_data("https://api.example.com")
    show(data)
}
```

### The Big Question: Fire-and-Forget Error Handling

**What happens when a `run_as_task` task crashes?**

#### Survey of Other Languages

| Language           | Default Behavior                          | Can Crash Program?                |
|--------------------|-------------------------------------------|-----------------------------------|
| **Go**             | Panic crashes entire program              | ✅ YES (must manually `recover()`) |
| **Rust (Tokio)**   | Task isolated, panic contained            | ❌ NO (configurable)               |
| **Node.js 15+**    | Crashes on unhandled rejection            | ✅ YES                             |
| **Python asyncio** | Warning logged, continues                 | ❌ NO                              |
| **C# (.NET 4.5+)** | Swallowed silently                        | ❌ NO                              |
| **Erlang/Elixir**  | Isolated + supervisor restarts            | ❌ NO (supervisor pattern)         |
| **Akka**           | Supervisor decides: restart/stop/escalate | ❌ NO (supervisor pattern)         |

#### Options for RazorForge/Suflae

| Option                    | Behavior                              | Pros                         | Cons                                 |
|---------------------------|---------------------------------------|------------------------------|--------------------------------------|
| **A. Crash program**      | One task failure kills all            | Simple, errors can't hide    | Too brutal for production            |
| **B. Isolated + logged**  | Task crashes logged, others continue  | Simple, resilient            | Errors may go unnoticed              |
| **C. Linked tasks**       | Caller + task crash together          | Predictable                  | Fire-and-forget still crashes caller |
| **D. Supervisor pattern** | Parent decides: restart/stop/escalate | Most flexible, battle-tested | Complex to implement                 |
| **E. Two variants**       | Default isolated, `!` variant crashes | User chooses                 | Two ways to do same thing            |

#### Suggested Design

**Default: Isolated + logged (Option B)** with optional strict mode:

```razorforge
# Default: Isolated - crash logged, others continue
run_as_task(fetch_data(url))

# Strict mode: If task crashes, caller crashes too
run_as_linked_task(critical_operation())
```

Or using `!` suffix convention:

```razorforge
# Default: Isolated
run_as_task(fetch_data(url))

# Strict: Crash program on failure
run_as_task!(critical_operation())
```

### Additional Open Questions

| Issue                 | Question                                                                              |
|-----------------------|---------------------------------------------------------------------------------------|
| **Return value**      | `run_as_task` is fire-and-forget. Should there be `spawn_task` that returns a handle? |
| **Cancellation**      | How to cancel a running task? Need `TaskHandle` with `.cancel()` method?              |
| **Task limits**       | What if thousands of tasks spawned? Backpressure mechanism?                           |
| **Only suspended?**   | Can only `suspended` routines be passed to `run_as_task`? (Probably yes)              |
| **Function coloring** | `suspended` creates async/sync divide. Accepted tradeoff?                             |
| **Suflae actors**     | How does `suspended`/`waitfor` interact with actor model?                             |

### Suflae-Specific: Actor Model Interaction

In Suflae's actor model, entities can be shared as actors:

```suflae
let counter = Counter(count: 0).share()  // Becomes an actor
counter.increment()  // Message send (async under the hood)
```

**Questions:**

1. Are actor method calls implicitly `waitfor`?
2. Can actors use `suspended` routines internally?
3. How do `run_as_task` and actors interact?

### Related Docs

- [RazorForge Routines](../wiki/RazorForge-Routines.md) - Suspended routine section
- [Suflae Routines](../wiki/Suflae-Routines.md) - Suspended routine section
- [Suflae Concurrency Model](../wiki/Suflae-Concurrency-Model.md) - Actor model
- [Keyword Comparison](../wiki/Keyword-Comparison.md) - `suspended`=`async`, `waitfor`=`await`

### References

**Go:**

- [Go Panic and Recover](https://yourbasic.org/golang/recover-from-panic/)
- [Go Forum: Why terminate on panic](https://forum.golangbridge.org/t/why-does-go-terminate-the-whole-process-if-one-goroutine-paincs/27122)

**Rust:**

- [Tokio UnhandledPanic](https://docs.rs/tokio/latest/tokio/runtime/enum.UnhandledPanic.html)
- [Tokio Spawning](https://tokio.rs/tokio/tutorial/spawning)

**Node.js:**

- [Unhandled Promise Rejections](https://thecodebarbarian.com/unhandled-promise-rejections-in-node.js.html)

**Python:**

- [asyncio Task Exceptions](https://superfastpython.com/asyncio-task-exceptions/)

**C#:**

- [Tasks and Unhandled Exceptions](https://devblogs.microsoft.com/dotnet/tasks-and-unhandled-exceptions/)

**Actor Model:**

- [Akka Fault Tolerance](https://doc.akka.io/libraries/akka-core/current/fault-tolerance.html)
- [Akka.NET Supervision](https://getakka.net/articles/concepts/supervision.html)
- [Elixir Task Documentation](https://hexdocs.pm/elixir/Task.html)

---

## 8. Lambda/Closure Capture Semantics

**Status:** Open
**Date:** 2025-12-31

### The Question

How do closures capture variables from their enclosing scope?

### Current State

**Partially documented in itertools context:**

- Lambdas CAN capture `Shared<T>`, `Tracked<T>`, `Snatched<T>`
- Lambdas CANNOT capture raw owned `entity` (violates ownership model)
- Captures must respect memory token system

**NOT documented:**

- General capture semantics (by value vs by reference)
- Lifetime guarantees for captures
- Mutable capture behavior

### The Problem

```razorforge
var counter = 0
let increment = () => { counter += 1 }
increment()
show(counter)  # 0 or 1? Depends on capture semantics
```

### Options

| Option                  | Behavior                                     | Pros                   | Cons                         |
|-------------------------|----------------------------------------------|------------------------|------------------------------|
| **A. By value (copy)**  | Captured variables copied at lambda creation | Safe, no aliasing      | Can't mutate outer scope     |
| **B. By reference**     | Captured variables referenced                | Can mutate outer scope | Lifetime issues (RazorForge) |
| **C. Explicit capture** | `[x, &y]` syntax like C++                    | Maximum control        | More syntax complexity       |
| **D. Hybrid**           | Immutable by value, mutable by reference     | Intuitive              | Magic behavior               |

### Language Comparison

| Language       | Capture Semantics                            |
|----------------|----------------------------------------------|
| **Python**     | By reference (late binding)                  |
| **JavaScript** | By reference                                 |
| **Rust**       | Inferred (move/borrow), explicit `move`      |
| **C++**        | Explicit `[x, &y, =, &]`                     |
| **Swift**      | By reference (with `[weak self]` for cycles) |
| **Java**       | By value (effectively final only)            |

### Suflae vs RazorForge Considerations

**Suflae (GC-based):**

- Reference capture is safe (GC handles lifetimes)
- Simpler model possible

**RazorForge (ownership-based):**

- Reference capture has lifetime implications
- May need explicit capture or borrow rules
- Must integrate with token system

### Open Questions

1. Should capture semantics differ between Suflae and RazorForge?
2. How do captures interact with `var` vs `let` bindings?
3. Can closures outlive captured references? (escape analysis)
4. How do captures work with `Shared<T>` (does it increment refcount)?

### Related Docs

- [RazorForge Routines](../wiki/RazorForge-Routines.md) - Lambda section
- [RazorForge Itertools](../wiki/RazorForge-Itertools.md) - Capture rules for itertools

---

## 9. Generic Variance

**Status:** Open
**Date:** 2025-12-31

### The Question

Should generic types support variance (covariance/contravariance)?

### Current State

**NOT documented.** No mention of variance in wiki.

Currently documented:

- Type constraints: `requires T follows Protocol`
- Type kind constraints: `is record`, `is entity`, `is resident`
- Const generic constraints: `is UAddr`

### The Problem

```razorforge
entity Animal { }
entity Dog follows Animal { }

let dogs: List<Dog> = List<Dog>()
let animals: List<Animal> = dogs  # Allowed? (covariance)

routine feed(animals: List<Animal>) { }
feed(dogs)  # Allowed?
```

### Options

| Option                                   | Behavior                             | Pros             | Cons                        |
|------------------------------------------|--------------------------------------|------------------|-----------------------------|
| **A. Invariant only**                    | `List<Dog>` ≠ `List<Animal>`         | Safest, simplest | Less flexible               |
| **B. Use-site variance**                 | `List<out Animal>` for covariant use | Flexible         | Complex at call sites       |
| **C. Declaration-site variance**         | `entity List<out T>`                 | Cleaner usage    | Complex to define correctly |
| **D. Implicit covariance for read-only** | Covariant if only reading            | Intuitive        | Hard to enforce             |

### Language Comparison

| Language   | Variance Model                                    |
|------------|---------------------------------------------------|
| **Java**   | Use-site (`? extends T`, `? super T`)             |
| **C#**     | Declaration-site (`out T`, `in T`)                |
| **Kotlin** | Both (`out T` declaration, `out T` use-site)      |
| **Rust**   | Implicit based on usage (PhantomData for control) |
| **Go**     | Invariant (no generics variance)                  |
| **Swift**  | Invariant for most, covariant for some stdlib     |

### Variance Rules

If implemented:

| Position        | Covariant (`out T`) | Contravariant (`in T`) |
|-----------------|---------------------|------------------------|
| Return type     | ✅ Allowed           | ❌ Forbidden            |
| Parameter type  | ❌ Forbidden         | ✅ Allowed              |
| Read-only field | ✅ Allowed           | ❌ Forbidden            |
| Mutable field   | ❌ Forbidden         | ❌ Forbidden            |

### Recommendation

Start with **Option A (Invariant only)** for simplicity. Add variance later if needed.

### Related Docs

- [RazorForge Generics](../wiki/RazorForge-Generics.md)
- [RazorForge Protocols](../wiki/RazorForge-Protocols.md)

---

## 10. Testing Framework

**Status:** Open
**Date:** 2025-12-31

### The Question

What should the built-in testing framework look like?

### Current State

**Minimally documented.** Only mention: `testing` module exists in stdlib.

Missing:

- Test syntax and attributes
- Assertion functions
- Test discovery mechanism
- Running tests
- Test organization

### Options

#### Test Declaration

| Option                      | Syntax                         | Notes                    |
|-----------------------------|--------------------------------|--------------------------|
| **A. Attribute-based**      | `@test routine test_foo() { }` | Like Rust, Python pytest |
| **B. Naming convention**    | `routine test_foo() { }`       | Like Go                  |
| **C. Separate test entity** | `test "description" { }`       | Like Jest, Zig           |

#### Assertions

| Option                    | Syntax                                    | Notes                                 |
|---------------------------|-------------------------------------------|---------------------------------------|
| **A. Use `verify`**       | `verify x == y`                           | Already exists for runtime assertions |
| **B. Dedicated `expect`** | `expect(x).to_equal(y)`                   | More expressive, matcher-based        |
| **C. Both**               | `verify` for simple, `expect` for complex | Flexibility                           |

### Proposed Design

```razorforge
@test
routine test_addition() {
    verify 1 + 1 == 2
    verify 2 * 3 == 6
}

@test
routine test_list_operations() {
    let list = List<S32>()
    list.push(1)
    list.push(2)

    verify list.length() == 2
    verify list.get!(0) == 1
}

@test
@should_crash(FileNotFoundError)
routine test_missing_file() {
    File.open!("nonexistent.txt")
}
```

### Open Questions

1. How are tests discovered? (file pattern, attribute scan, explicit registration)
2. How are tests run? (`razorforge test`, separate test runner)
3. Test isolation? (each test fresh state, or shared setup)
4. Setup/teardown? (`@before`, `@after` attributes)
5. Test fixtures and parameterized tests?
6. Mocking/stubbing support?
7. Code coverage integration?

### Related Docs

- [Standard Libraries](../wiki/Standard-Libraries.md) - mentions `testing` module

---

## 11. String Interpolation Expression Limits

**Status:** Open
**Date:** 2025-12-31

### The Question

What expressions are allowed inside f-string interpolation `{}`?

### Current State

**Partially documented.** Examples show simple usage:

```razorforge
f"Value: {x}"
f"Hello {name}!"
```

**NOT documented:**

- Expression complexity limits
- Nested interpolation
- Format specifiers

### Options

| Option                    | Allowed Expressions                         | Pros                | Cons                             |
|---------------------------|---------------------------------------------|---------------------|----------------------------------|
| **A. Any expression**     | Identifiers, operators, calls, conditionals | Maximum flexibility | Complex parsing, potential abuse |
| **B. Simple expressions** | Identifiers, field access, method calls     | Readable            | Limited                          |
| **C. Identifiers only**   | Just `{x}`, no `{x.y}` or `{foo()}`         | Simplest            | Too restrictive                  |

### Expression Examples

```razorforge
# Level 1: Identifiers
f"{name}"                           # Definitely OK

# Level 2: Member access
f"{user.name}"                      # OK?
f"{point.x}"                        # OK?

# Level 3: Method calls
f"{list.length()}"                  # OK?
f"{name.to_uppercase()}"            # OK?

# Level 4: Operators
f"{a + b}"                          # OK?
f"{count * 2}"                      # OK?

# Level 5: Complex expressions
f"{if x > 0 then "positive" else "negative"}"  # OK?
f"{items.filter(x => x > 0).length()}"         # OK?

# Level 6: Nested interpolation
f"Result: {f"{x} + {y}"}"           # OK?
```

### Format Specifiers

Should format specifiers be supported?

```razorforge
f"{value:>10}"      # Right-align, width 10
f"{price:.2f}"      # 2 decimal places
f"{num:04d}"        # Zero-padded, width 4
f"{hex:x}"          # Hexadecimal
```

### Recommendation

**Level 3 (method calls)** seems like a good balance:

- `{x}` - identifiers ✅
- `{x.y}` - field access ✅
- `{x.method()}` - method calls ✅
- `{a + b}` - operators ❌ (use temp variable)
- Nested f-strings ❌

### Related Docs

- [RazorForge Text](../wiki/RazorForge-Text.md) - Text literals section
- [COMPILER-TODO](COMPILER-TODO.md) - F-String desugaring section

---

## 12. Module Re-exports

**Status:** Open
**Date:** 2025-12-31

### The Question

Should modules be able to re-export symbols from other modules?

### Current State

**Documented:** Transitive imports NOT visible - must import explicitly.

```razorforge
# If A imports B, and B imports C
# A cannot use C's symbols without explicit import
```

**NOT documented:** How to intentionally re-export.

### The Problem

```razorforge
# User wants:
import Collections

# And get List, Dict, Set without:
import Collections/List
import Collections/Dict
import Collections/Set
```

### Options

| Option                  | Syntax                              | Behavior                                 |
|-------------------------|-------------------------------------|------------------------------------------|
| **A. No re-exports**    | N/A                                 | Users must import each module explicitly |
| **B. `public import`**  | `public import Collections/List`    | Re-exports all public symbols            |
| **C. `export` keyword** | `export List from Collections/List` | Selective re-export                      |
| **D. Barrel files**     | `mod.rf` auto-exports submodules    | Convention-based                         |

### Language Comparison

| Language       | Re-export Mechanism                         |
|----------------|---------------------------------------------|
| **Rust**       | `pub use other_mod::Symbol;`                |
| **TypeScript** | `export { X } from './other'`               |
| **Python**     | `from .submodule import *` in `__init__.py` |
| **Go**         | No re-exports (explicit imports only)       |
| **Java**       | No re-exports                               |

### Proposed Design (if supported)

```razorforge
# In Collections/mod.rf
namespace Collections

# Re-export specific symbols
public import Collections/List using {List, MutableList}
public import Collections/Dict using {Dict}
public import Collections/Set using {Set}

# Now users can:
import Collections
let list = Collections.List<S32>()
```

### Arguments Against Re-exports

1. **Explicit is better** - Users know exactly where symbols come from
2. **Simpler mental model** - No transitive visibility confusion
3. **Faster compilation** - No need to resolve re-export chains
4. **Go's success** - Go has no re-exports and works fine

### Arguments For Re-exports

1. **Convenience** - One import for related functionality
2. **API stability** - Can reorganize internal structure without breaking users
3. **Common pattern** - Most modern languages support it

### Recommendation

Start with **Option A (No re-exports)** - matches current "explicit dependencies" philosophy.
Consider adding later if user demand is high.

### Related Docs

- [RazorForge Modules and Imports](../wiki/RazorForge-Modules-and-Imports.md)

---

## 13. Result/Lookup Unwrapping Ergonomics

**Status:** Open
**Date:** 2025-12-31

### The Question

Is the current `when` pattern for unwrapping `Result<T>` too verbose for common cases?

### The Problem

```suflae
# Current pattern - verbose for a common operation
let num = when result:
    is Crashable e:
        alert(f"Error: {e.crash_message()}")
        0  # Return default
    else value:
        show(f"Success: {value}")
        value
```

Issues:

1. Must handle error case first (enforced ordering)
2. Need to provide a return value from error branch
3. Verbose for "just give me the value or a default" pattern
4. The `else value` syntax for the success case is somewhat unintuitive
5. **The "trailing value as result" pattern looks ugly** - a bare `0` or `value` at the end of a block looks like a stray orphan, not an intentional return value
6. **No escape hatch via lambdas** - lambdas are single-expression only, no semicolons exist, so you can't do `e => { side_effect; value }` - must use named routines

### Common Patterns That Feel Clunky

**Generated variant functions (compiler creates these from `!` routines):**

```suflae
# Original failable routines:
routine parse!(text: Text) -> S32           # throws ParseError
routine get_item!(index: S32) -> Item       # absent if not found
routine find_user!(id: S64) -> User         # throws OR absent

# Compiler generates:
routine try_parse(text: Text) -> Maybe<S32>     # absent only → Maybe
routine check_parse(text: Text) -> Result<S32>  # throw only → Result
routine try_get_item(index: S32) -> Maybe<Item> # absent only → Maybe
routine find_user(id: S64) -> Lookup<User>      # both → Lookup
```

**Pattern 1: Maybe<T> from try_ (absent only)**
```suflae
let item = when try_get_item(index):
    is None => default_item
    else v => v
```

**Pattern 2: Result<T> from check_ (throw only)**
```suflae
let num = when check_parse(input):
    is Crashable e => 0
    else v => v

# With side effect - ugly trailing value
let num = when check_parse(input):
    is Crashable e:
        alert(e.crash_message())
        0
    else v => v
```

**Pattern 3: Lookup<T> from find_ (both throw and absent)**
```suflae
# Most verbose - must handle Error, None, AND Value
let user = when find_user(id):
    is Crashable e:
        alert(e.crash_message())
        default_user
    is None:
        alert("User not found")
        default_user
    else u => u
```

**Pattern 4: Transform value**
```suflae
let doubled = when check_parse(input):
    is Crashable e => 0
    else v => v * 2
```

### Other Languages

| Language    | Syntax                            | Notes                                    |
|-------------|-----------------------------------|------------------------------------------|
| **Rust**    | `result.unwrap_or(default)`       | Method-based                             |
| **Rust**    | `result.unwrap_or_else(\|\| ...)` | Lazy default                             |
| **Rust**    | `result?`                         | Propagate error (in Result-returning fn) |
| **Swift**   | `try? expr ?? default`            | Optional + nil coalescing                |
| **Kotlin**  | `result.getOrElse { default }`    | Method-based                             |
| **Haskell** | `fromMaybe default maybe`         | Function-based                           |

### Possible Solutions

**Option A: Method-based unwrapping**

```suflae
# Simple default
let num = check_parse(input).or_default(0)

# Lazy default with error access - BUT this requires multiline lambda!
# Current language: lambdas are single-expression only
# This syntax is NOT currently valid:
# let num = check_parse(input).or_else(e => {
#     alert(e.crash_message())
#     0
# })

# Would need a named routine instead:
routine handle_parse_error(e: Crashable) -> S32 {
    alert(e.crash_message())
    return 0
}
let num = check_parse(input).or_else(handle_parse_error)

# Transform success value
let doubled = check_parse(input)
    .select(v => v * 2)
    .or_default(0)
```

**Option B: Operator-based**

```suflae
# Result coalescing (like ?? for Maybe)
let num = check_parse(input) ?? 0

# With error handling - NOT valid (no multiline lambdas, no semicolons)
# let num = check_parse(input) ?? (e => { alert(e.crash_message()); 0 })  # ERROR

# Would need named routine:
let num = check_parse(input) ?? handle_parse_error
```

**Complication: Lookup<T> has THREE cases**

`Lookup<T>` can be: Error | None | Value - the `??` operator becomes ambiguous:

```suflae
# What does ?? mean for Lookup<T>?
let result = find_user(id) ?? default_user

# Does it handle:
# - Error → default_user?
# - None → default_user?
# - Both Error AND None → default_user?
```

Possible solutions for Lookup:
```suflae
# Separate operators?
let result = find_user(id) ?? default_user      # None → default
let result = find_user(id) ?! default_user      # Error → default
let result = find_user(id) ??? default_user     # Both → default (ugly)

# Or method-based (clearer):
let result = find_user(id).or_if_none(default_user)
let result = find_user(id).or_if_error(default_user)
let result = find_user(id).or_default(default_user)  # Both
```

**Option C: Keep current, add convenience methods**

```suflae
# Current when syntax for complex cases
let num = when check_parse(input):
    is Crashable e => handle_complex_error(e)
    else v => v

# Convenience methods for simple cases
let simple = check_parse(input).or_default(0)
let alerted = check_parse(input).or_alert(default: 0)
```

**Option D: Different else syntax**

```suflae
# Maybe: else binds the unwrapped value
let num = when try_parse(input):
    is None => 0
    else v => v  # v is the value

# Result: else binds the unwrapped value (same pattern)
let num = when check_parse(input):
    is Crashable e => 0
    else v => v  # v is the value

# Alternative: use 'is T' for explicit type
let num = when check_parse(input):
    is Crashable e => 0
    is S32 v => v  # More explicit but redundant
```

### The "Trailing Value" Problem

The "last expression is the result" pattern looks ugly in both languages:

```suflae
# Suflae - looks like a stray value
is Crashable e:
    alert(f"Error: {e.crash_message()}")
    0  # What is this?
```

```razorforge
# RazorForge - braces don't really help
is Crashable e => {
    alert(f"Error: {e.crash_message()}")
    0  # Still looks weird
}
```

The bare value at the end of a block doesn't read as "return this" - it looks like a mistake.

**Possible solutions for trailing value:**

**Option E: Explicit `then` keyword**
```suflae
is Crashable e:
    alert(f"Error: {e.crash_message()}") then 0

# Or multi-line
is Crashable e:
    alert(f"Error: {e.crash_message()}")
    then 0
```

**Option F: Require `return` in expression blocks**
```suflae
is Crashable e:
    alert(f"Error: {e.crash_message()}")
    return 0  # Explicit, but feels wrong for expression context
```

**Option G: Discourage this pattern entirely**
- Document that `when` expressions with side effects should use named routines
- Reserve `when` for pure pattern matching
- Use `.or_else(named_error_handler)` for side effects + value
- **Problem:** This is verbose - requires defining a separate routine for every error handler

### Questions to Resolve

1. Should we add `.or_default()` / `.or_else()` methods to Result/Lookup?
2. Should there be an operator like `?!` for Result unwrapping with default?
3. Is the `else value` pattern for the success case intuitive enough?
4. Should the success case use `is T value` instead of `else value`?
5. **How to handle "do something AND return a value" in Suflae's indentation syntax?**
   - `then` keyword?
   - Require explicit `return`?
   - Discourage the pattern entirely?

### Related Docs

- [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md)
- [Suflae Error Handling](../wiki/Suflae-Error-Handling.md)
- [RazorForge Pattern Matching](../wiki/RazorForge-Pattern-Matching.md)

---

## Template for Future TODOs

```markdown
## N. Title

**Status:** Open / In Progress / Resolved
**Date:** YYYY-MM-DD

### The Question

[What needs to be decided]

### Current State

[What the current implementation/design is]

### The Problem

[Why this needs a decision]

### Considerations

[Pros/cons of different approaches]

### Open Questions

[Specific questions to answer]

### Related Docs

[Links to relevant documentation]
```
