# Suflae/RazorForge Design TODOs

This document tracks open design questions that need to be resolved.

For resolved decisions, see [DESIGN-DECISIONS.md](archive/DESIGN-DECISIONS.md).

---

## 1. Generator Routines (`generate` keyword)

**Status:** ✅ Resolved
**Date:** 2025-12-31
**Resolved:** 2026-01-04

### The Question

How should generator routines work in RazorForge and Suflae?

### Design Decision

**Implicit conversion**: Any routine using `generate` becomes a generator. Return type must be `Sequence<T>`.

**Generators are pure iteration machines, not closures.** They cannot capture variables, delegate to other sequences, or
receive values back. This keeps them simple and predictable.

```razorforge
routine fibonacci() -> Sequence<S32> {
    let a = 0
    let b = 1
    loop {
        generate a
        (a, b) = (b, a + b)
    }
}

for num in fibonacci() {
    if num > 100 {
        break
    }
    show(num)
}
```

### Resolved Questions

| Issue                     | Decision                                                                                  |
|---------------------------|-------------------------------------------------------------------------------------------|
| **Return type mismatch**  | **CE**: routine using `generate` must return `Sequence<T>`                                |
| **Mixed return/generate** | `return`, `throw`, or `absent` ends iteration                                             |
| **Nested lambdas**        | **CE**: `generate` only valid at generator routine level, not inside lambdas/closures     |
| **State storage**         | Heap-allocated coroutine frame, single allocation when generator created, NOT thread-safe |
| **Error handling**        | Iteration stops, runtime error propagates to caller                                       |
| **Captures**              | **No** — generators cannot capture variables; pass state as parameters instead            |
| **Delegation**            | **No** — no `generate from seq` syntax; use explicit loops                                |
| **Infinite generators**   | **Yes** — allowed (fibonacci example)                                                     |
| **Bidirectional**         | **No** — one-way only, no `send()` equivalent                                             |
| **Cleanup**               | **N/A** — token system prevents generators from holding resources across yields           |

### Naming

- `Iterator<T>` renamed to `Sequence<T>` (represents a sequence of values)
- `Itertools` renamed to `SeqTools` (methods operating on sequences)

### Design Rationale

**No captures:** Generators are pure iteration, not closures. If you need state, pass it as parameters:

```razorforge
# ✅ Pass state as parameter
routine count_from(start: S32) -> Sequence<S32> {
    var n = start
    loop {
        generate n
        n += 1
    }
}
```

**No delegation:** Use explicit loops for clarity:

```razorforge
# ✅ Explicit loop
routine flatten(lists: List<List<S32>>) -> Sequence<S32> {
    for list in lists {
        for item in list {
            generate item
        }
    }
}
```

**Cleanup handled by token system:** Resources accessed via `viewing`/`hijacking` blocks are scope-bound tokens that
cannot escape. Generators naturally can't hold resources across yields because tokens can't be stored or returned.

### Related Docs

- [RazorForge Routines](https://razorforge.lumi-dev.xyz/Routines.md)
- [Suflae Routines](https://suflae.lumi-dev.xyz/Routines.md)
- [RazorForge SeqTools](https://razorforge.lumi-dev.xyz/SeqTools.md)
- [Suflae SeqTools](https://suflae.lumi-dev.xyz/SeqTools.md)

---

## 2. Concurrency Model (suspended/threaded routines)

**Status:** ✅ Resolved
**Date:** 2025-12-30
**Resolved:** 2026-01-04

### The Question

How should async routines, OS threads, awaiting (`waitfor`), and task spawning work?

### Design Decision

**Unified concurrency model** with two routine modifiers:

- `suspended routine` → Green thread (lightweight, I/O-bound)
- `threaded routine` → OS thread (heavyweight, CPU-bound)

Both use the same `Task<T>` return type and `waitfor` keyword.

### Resolved Questions

| Issue                      | Decision                                                                        |
|----------------------------|---------------------------------------------------------------------------------|
| **`suspended` keyword**    | Kept — marks routines that run on green threads                                 |
| **`threaded` keyword**     | **New** — marks routines that run on OS threads                                 |
| **`waitfor` keyword**      | Kept — waits for task completion, works with both thread types                  |
| **`until` keyword**        | **New** — timeout for waitfor: `waitfor task until 5s`                          |
| **Task spawning**          | Implicit — calling without `waitfor` returns `Task<T>`                          |
| **Fire-and-forget**        | Blank-returning routines auto-spawn, `Task<Blank>` discarded                    |
| **Cancellation**           | Cooperative — `task.cancel()` sets flag, routine checks `is_cancelled()`        |
| **Structured concurrency** | Yes — cancelling parent cancels all child tasks                                 |
| **Progress reporting**     | Optional — `report_progress(F64)` inside routine, `task.try_progress()` outside |
| **Function coloring**      | Accepted — `suspended`/`threaded` creates explicit divide                       |

### Calling Concurrent Routines

| Call Pattern                | Returns     | Thread    | Behavior                |
|-----------------------------|-------------|-----------|-------------------------|
| `waitfor fetch(url)`        | `T`         | Same      | Blocks until done       |
| `fetch(url)`                | `Task<T>`   | New green | Runs in parallel        |
| `waitfor compute(data)`     | `T`         | Same      | Blocks until done       |
| `compute(data)`             | `Task<T>`   | New OS    | Runs in parallel        |
| `send_email(to)` (Blank)    | discarded   | New       | Fire-and-forget         |
| `waitfor task until 5s`     | `T?`        | —         | Timeout, returns `none` |
| `waitfor (t1, t2)`          | `(T1, T2)`  | —         | Wait for multiple       |
| `waitfor (t1, t2) until 5s` | `(T1, T2)?` | —         | Multiple with timeout   |

### Task<T> Type

`Task<T>` is a **record** (handle) wrapping runtime-managed state:

```razorforge
record Task<T>:
    # Status
    routine is_done() -> Bool
    routine is_cancelled() -> Bool
    routine is_running() -> Bool

    # Control
    routine cancel()

    # Progress (returns none if routine doesn't report progress)
    routine try_progress() -> F64?
```

### Error Handling Integration

Error handling is chosen at spawn time using the standard `!`/`try_`/`check_`/`lookup_` variants:

```razorforge
# Spawning with different error handling
let t1 = compute!(data)        # Task<S64> — crashes on error
let t2 = try_compute(data)     # Task<S64?> — none on error/cancel
let t3 = check_compute(data)   # Task<Result<S64>> — error info preserved
let t4 = lookup_compute(data)  # Task<Lookup<S64>> — error/absent/value

# Waiting just extracts the value
let result = waitfor t2  # S64? — error handling was decided at spawn
```

### Cooperative Cancellation

```razorforge
threaded routine compute!(data: List<S64>) -> S64:
    var sum: S64 = 0
    let total = data.count()

    for i, item in data.enumerate():
        if is_cancelled():
            absent  # Becomes none in try_, error in check_

        report_progress(F64(i) / F64(total))
        sum += process(item)

    return sum

# Usage
let task = compute(data)
task.cancel()  # Sets flag, routine checks on next iteration

let result = waitfor try_compute(data)
when result:
    is none => show("Cancelled or failed")
    else value => show(f"Got: {value}")
```

### Structured Concurrency

Cancelling a parent task cancels all child tasks:

```razorforge
threaded routine parent!() -> S64:
    let child1 = compute(data1)  # Child task
    let child2 = compute(data2)  # Child task
    return waitfor child1 + waitfor child2

let task = parent()
task.cancel()  # Cancels parent AND child1, child2
```

### Timeout

```razorforge
let task = compute(data)
let result = waitfor task until 5s  # Returns S64?

when result:
    is none => show("Timed out")
    else value => show(f"Result: {value}")
```

Timeout automatically calls `task.cancel()` when time expires.

### Example

```razorforge
# Green thread (I/O-bound)
suspended routine fetch(url: Text) -> Text:
    return waitfor http.get(url)

# OS thread (CPU-bound)
threaded routine compute(data: List<S64>) -> S64:
    var sum: S64 = 0
    for item in data:
        if is_cancelled():
            absent
        sum += process(item)
    return sum

routine start():
    # Sequential
    let a = waitfor fetch("url1")
    let b = waitfor compute(data)

    # Parallel (mixed green + OS threads)
    let t1 = fetch("url1")       # Task<Text>, green thread
    let t2 = compute(data)       # Task<S64>, OS thread
    let (text, num) = waitfor (t1, t2)

    # With timeout
    let result = waitfor compute(big_data) until 10s

    # Fire-and-forget
    send_email("user@example.com")  # Spawns, returns immediately

    # Progress tracking
    let task = compute(huge_data)
    while task.is_running():
        let p = task.try_progress() ?? 0.0
        show(f"Progress: {p * 100}%")
        sleep(100ms)
    let final = waitfor task
```

### Stdlib Primitives

In addition to core language features, the concurrency stdlib provides:

| Primitive      | Purpose             | Key Feature                          |
|----------------|---------------------|--------------------------------------|
| `Channel<T>`   | Message passing     | Point-to-point, buffered/unbuffered  |
| `SignalCaster` | Condition variables | **Broadcast** to all waiting threads |

**Channel API:** `send!/try_send/check_send`, `receive!/try_receive/check_receive` with `until` timeout.

**SignalCaster API:** `wait(guard)`, `wait_while(guard, pred)`, `cast_one()`, `cast_all()`.

SignalCaster exists specifically for broadcast capability — it's the only way to wake ALL waiting threads at once.

### Related Docs

- [RazorForge Concurrency Model](https://razorforge.lumi-dev.xyz/Concurrency-Model.md)
- [RazorForge Concurrency Primitives](https://razorforge.lumi-dev.xyz/Concurrency-Primitives.md) — Channel, SignalCaster
- [Suflae Concurrency Model](https://suflae.lumi-dev.xyz/Concurrency-Model.md)
- [RazorForge Routines](https://razorforge.lumi-dev.xyz/Routines.md)

---

## 3. Lambda/Closure Capture Semantics

**Status:** ✅ Resolved
**Date:** 2025-12-31

### The Question

How do closures capture variables from their enclosing scope?

### Design Decision

**Explicit capture with `given` keyword.** Captures are required, by name, from immediate scope only.

```razorforge
# Syntax: (params) given (captures) => expr
let threshold = 100
let data = MyData().share()

# Single capture
let check = x given threshold => x > threshold

# Multiple captures
let process = (x, y) given (data, threshold) => x.value > threshold and y.check(data)

# No captures - 'given' omitted
let double = x => x * 2

# Comparison chaining supported
let in_range = x given (min, max) => min <= x < max
```

### Resolved Questions

| Issue                    | Decision                                                   |
|--------------------------|------------------------------------------------------------|
| **Capture syntax**       | `given` keyword (not `with` or `in`)                       |
| **Capture scope**        | Immediate routine scope only (not outer nested scopes)     |
| **Required vs optional** | Required - CE if variable used but not declared in `given` |
| **By name**              | Captures matched by name, not position                     |
| **Parentheses**          | Optional for single param/capture: `x given y => expr`     |

### Capture Behavior by Type

**RazorForge:**

- `Shared<T>`, `Tracked<T>`, `Snatched<T>` - Reference captured (refcount++)
- Value types (`record`, `variant`, `choice`) - Copied by value
- **Cannot capture:** Raw `entity`/`resident`, scope-bound tokens (`Viewed`, `Hijacked`, etc.)

**Suflae:**

- `record`, `variant`, `choice` - Copied by value
- `entity` - Reference shared (refcount++)
- `shared entity` - Thread-safe reference (atomic refcount)

### No Nested Routines

Nested routine definitions are banned. Use module-level private routines instead.

```razorforge
# ❌ BANNED
routine outer() {
    routine inner() { }  # CE: Nested routines not allowed
}

# ✅ OK
private routine helper(x: S32) -> S32 { return x * 2 }
routine outer() { let result = helper(5) }
```

### Related Docs

- [RazorForge Routines](https://razorforge.lumi-dev.xyz/Routines.md)
- [Suflae Routines](https://suflae.lumi-dev.xyz/Routines.md)
- [RazorForge SeqTools](https://razorforge.lumi-dev.xyz/SeqTools.md)

---

## 4. Generic Variance

**Status:** ✅ Resolved
**Date:** 2025-12-31
**Resolved:** 2026-01-04

### The Question

Should generic types support variance (covariance/contravariance)?

### Design Decision

**Invariant only — N/A without inheritance.**

RazorForge/Suflae has no entity inheritance. Entities use `follows` for protocol conformance, which is interface
implementation, not subtyping. Without subtype relationships, variance is irrelevant:

```razorforge
protocol Animal { }
entity Dog follows Animal { }  # Dog FOLLOWS Animal, not IS-A Animal
entity Cat follows Animal { }

# Dog and Cat are completely unrelated types
# List<Dog> and List<Cat> have no relationship by definition
# Variance only matters when types form hierarchies — they don't here
```

### Why This Works

| Concept                       | With Inheritance          | Without Inheritance (RazorForge)                |
|-------------------------------|---------------------------|-------------------------------------------------|
| `Dog` and `Animal`            | Dog IS-A Animal (subtype) | Dog FOLLOWS Animal (conformance)                |
| `List<Dog>` vs `List<Animal>` | Variance question         | N/A - Animal is a protocol, not a concrete type |
| `List<Dog>` vs `List<Cat>`    | Related via Animal        | Completely unrelated types                      |

**Generics are invariant by definition** — no design decision needed.

### Related Docs

- [RazorForge Generics](https://razorforge.lumi-dev.xyz/Generics.md)
- [RazorForge Protocols](https://razorforge.lumi-dev.xyz/Protocols.md)

---

## 4b. Type Erasure (`Erased<T>`)

**Status:** ✅ Resolved
**Date:** 2026-01-04

### The Question

Without inheritance, how do we support polymorphic collections (e.g., a list of different types that all follow the
same protocol)?

### Design Decision

**`Erased<T>` is a compiler primitive that only accepts protocols.**

```razorforge
# Valid - Widget is a protocol
let widgets: List<Erased<Widget>> = List()
widgets.add_last(button.erase())   # Button follows Widget
widgets.add_last(label.erase())    # Label follows Widget

# Compile error - S32 is not a protocol
let nums: List<Erased<S32>> = List()  # CE: S32 is not a protocol

# Compile error - Button is an entity, not a protocol
let btns: List<Erased<Button>> = List()  # CE: Button is not a protocol
```

### How It Works

`Erased<T>` is a **fat pointer** (two pointers):

1. **Data pointer** — Points to the heap-allocated concrete instance
2. **VTable pointer** — Points to the method table for the protocol

```
┌─────────────────┐
│  Erased<Widget> │
├─────────────────┤
│ data: ──────────┼──► [Button instance on heap]
│ vtable: ────────┼──► [Widget vtable for Button]
└─────────────────┘
```

The compiler generates:

- Allocation code when calling `.erase()` on a concrete type
- A vtable for each (ConcreteType, Protocol) pair
- Dynamic dispatch through the vtable for protocol method calls
- Proper destructor invocation when `Erased<T>` is dropped

### Constraint

```
Erased<T> where T is protocol
```

This is enforced at compile time. Only protocol types are valid type arguments.

### Why Compiler Primitive

`Erased<T>` cannot be composed from other wrapper types like `Snatched<T>` because:

1. `Snatched<T>` requires knowing `T` at compile time
2. Type erasure means we don't know the concrete type statically
3. The vtable is static data, not heap-allocated

### Language Difference

| Language   | Erasure                                |
|------------|----------------------------------------|
| RazorForge | Explicit — `Erased<T>` wrapper         |
| Suflae     | Automatic — protocol types auto-erased |

### Related Docs

- [RazorForge Protocols](https://razorforge.lumi-dev.xyz/Protocols.md)
- [RazorForge Generics](https://razorforge.lumi-dev.xyz/Generics.md)

---

## 5. Testing Framework

**Status:** Open
**Date:** 2025-12-31

### The Question

What should the built-in testing framework look like?

### Proposed Design

```razorforge
@test
routine test_addition() {
    verify!(1 + 1 == 2)
    verify!(2 * 3 == 6)
}

@test
routine test_list_operations() {
    let list = List<S32>()
    list.add_last(1)
    list.add_last(2)

    verify!(list.count() == 2)
    verify!(list[0] == 1)
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

- [Standard Libraries](https://razorforge.lumi-dev.xyz/Standard-Libraries.md)

---

## 6. String Interpolation Expression Limits

**Status:** ✅ Resolved
**Date:** 2025-12-31
**Resolved:** 2026-01-04

### The Question

What expressions are allowed inside f-string interpolation `{}`?

### Design Decision

**Level 3 (method calls)** — identifiers, field access, and method calls allowed. Operators and complex expressions
require temp variables.

### Allowed Expressions

```razorforge
f"{name}"           # ✅ Identifiers
f"{user.name}"      # ✅ Field access
f"{list.count()}"   # ✅ Method calls
f"{a + b}"          # ❌ CE: Use temp variable
f"{if x > 0 ...}"   # ❌ CE: Use temp variable
```

### Workaround for Complex Expressions

```razorforge
# ❌ Not allowed
f"Result: {a + b * c}"

# ✅ Use temp variable
let result = a + b * c
f"Result: {result}"
```

### Rationale

- **Readability:** F-strings should be readable at a glance
- **Parsing simplicity:** No need to handle operator precedence inside `{}`
- **Encourages clarity:** Complex logic belongs in named variables

### Related Docs

- [RazorForge Text](https://razorforge.lumi-dev.xyz/Text.md)
- [COMPILER-TODO](COMPILER-TODO.md)

---

## 7. Block Result Keyword (`becomes`)

**Status:** ✅ Resolved
**Date:** 2025-12-31
**Resolved:** 2026-01-07

### The Question

How to handle "do something AND return a value" in multi-statement branches? The trailing value pattern looks like a
bug:

```suflae
is Crashable e:
    alert(f"Error: {e.crash_message()}")
    0  # Looks like stray value, not intentional result
```

### Design Decision

**`becomes` keyword for explicit block results in multi-statement branches.**

```suflae
let num = when result:
    is Crashable e:
        show(f"Error: {e.crash_message()}")
        becomes 0
    else value:
        show(f"Success: {value}")
        becomes value
```

### Resolved Questions

| Issue                     | Decision                                                            |
|---------------------------|---------------------------------------------------------------------|
| **Keyword choice**        | `becomes` — reads naturally ("this block becomes 0")                |
| **Single expressions**    | Use `=>` syntax: `is none => 0`                                     |
| **Multi-statement**       | Require `becomes` when block has statements AND produces a value    |
| **Stray value detection** | CE if trailing value without `becomes` (catches accidental bugs)    |
| **No conflict**           | `becomes` is unique, doesn't conflict with generators or other uses |

### Why `becomes`

Considered alternatives:

- `yield` — too confusing, commonly associated with generators in other languages
- `then` — conflicts with `if...then...else`
- `result` — conflicts with `Result<T>` type name

`becomes` reads naturally: "handle error, becomes 0" = "this block becomes 0"

### Grammar

```
block_result := 'becomes' expression
```

### Examples

```suflae
# ✅ Single expression - use =>
is none => 0

# ✅ Multi-statement - use becomes
is Crashable e:
    log_error(e)
    becomes default_value

# ❌ CE: Stray value without 'becomes'
is Crashable e:
    log_error(e)
    0  # Error: use 'becomes 0' or remove this line
```

### Related Docs

- [RazorForge Error Handling](https://razorforge.lumi-dev.xyz/Error-Handling.md)
- [Suflae Error Handling](https://suflae.lumi-dev.xyz/Error-Handling.md)

---

## 8. Suflae Actor Field Access for SeqTools

**Status:** ✅ Resolved
**Date:** 2025-12-31
**Resolved:** 2026-01-02

### The Question

How should Suflae's actor type interact with itertools operations?

### Design Decision

**Option D: Strict actor isolation.** Field access is forbidden on actors. Must use methods.

**Naming decision:** `Actor<T>` with `.act()` method.

This differentiates Suflae's actor model from RazorForge's:

- `Shared<T>` (RazorForge) - Single-threaded reference counting
- `Shared<T, Policy>` (RazorForge) - Multi-threaded with locks (Arc+Lock)
- `Actor<T>` (Suflae) - Actor model with message passing

### Resolved Questions

| Issue                  | Decision                                                                 |
|------------------------|--------------------------------------------------------------------------|
| **Naming**             | `Actor<T>` with `.act()` method (standard actor model terminology)       |
| **Field access**       | **CE**: Cannot access fields on `Actor<T>`, must use methods             |
| **Zero-copy views**    | Impossible in actor model; use RazorForge locks if zero-copy is required |
| **SeqTools on actors** | Define methods on entity that perform operations internally              |

### Example

```suflae
entity Counter:
    var value: S32

routine Counter.get_value() -> S32:
    return me.value

routine Counter.increment():
    me.value += 1

let counter = Counter(value: 0).act()  # Type: Actor<Counter>

counter.increment()                     # Fire-and-forget message
let val = counter.get_value()           # Sends message, waits for response

# Field access forbidden:
# counter.value        # CE: Cannot access fields on Actor<T>
# counter.value = 10   # CE: Cannot access fields on Actor<T>
```

### Rationale

- **Consistency:** Actor isolation is the core value proposition - hidden copies would violate this
- **Simplicity:** One rule: "Actors use methods, not fields" - no exceptions
- **Clarity:** If you need shared data with direct field access, use RazorForge's `Shared<T, Mutex>` instead
- **Performance:** No hidden copies; explicit method calls make data flow visible
- **API design:** Forces good actor API design - methods that encapsulate operations

### Related Docs

- [Suflae Concurrency Model](https://suflae.lumi-dev.xyz/Concurrency-Model.md)
- [Suflae SeqTools](https://suflae.lumi-dev.xyz/SeqTools.md)

---

## 9. Zip Return Type

**Status:** ⏳ Open
**Date:** 2025-12-31

### The Question

What type does `zip` return when combining two sequences?

### Current Docs

The SeqTools docs say:

```razorforge
seq1.zip(with: seq2)  # Combine parallel elements into records
```

But what exactly is that "record"?

### Proposed Design

**Heterogeneous operations (`zip`, `product`):** Use `Zipped<A, B, ...>` variadic generic

```razorforge
# Zipped<A, B> for two sequences
let pairs: Sequence<Zipped<S32, Text>> = numbers.zip(with: names)
for z in pairs {
    show(f"{z.0}: {z.1}")  # Indexed access?
}
for (num, name) in pairs {  # Destructuring
    show(f"{num}: {name}")
}

# Zipped<A, B, C> for three sequences
let triples: Sequence<Zipped<S32, Text, Bool>> = a.zip(with: b, with: c)

# Same for product
let grid: Sequence<Zipped<S32, S32>> = rows.product(with: cols)
```

**Homogeneous operations (`combinations`, `permutations`):** Use `List<T>`

```razorforge
# combinations/permutations pick from same type → List<T>
let pairs: Sequence<List<S32>> = [1, 2, 3].combinations(pick: 2)
# pairs: [[1, 2], [1, 3], [2, 3]]

let perms: Sequence<List<S32>> = [1, 2, 3].permutations(pick: 2)
# perms: [[1, 2], [1, 3], [2, 1], [2, 3], [3, 1], [3, 2]]

for combo in [1, 2, 3].combinations(pick: 2) {
    show(combo)  # List<S32>
}
```

### Product vs Permutations with Replacement

These are mathematically similar but semantically different:

```razorforge
# product: combines DIFFERENT sequences → Zipped<A, B>
[1, 2].product(with: ["a", "b"])
# → Sequence<Zipped<S32, Text>>: (1,"a"), (1,"b"), (2,"a"), (2,"b")

# multiarrange: picks from SAME sequence → List<T>
[1, 2].multiarrange(pick: 2)
# → Sequence<List<S32>>: [1,1], [1,2], [2,1], [2,2]
```

**Keep separate** for type consistency - even when values match, return types differ.

### Naming: Math-Inspired Names

**Proposed renaming:**

| Old Name                        | New Name          | Description                        |
|---------------------------------|-------------------|------------------------------------|
| `combinations(pick: n)`         | `choose(n)`       | n-combinations without replacement |
| `combinations_with_replacement` | `multichoose(n)`  | n-combinations with replacement    |
| `permutations(pick: n)`         | `arrange(n)`      | n-permutations without replacement |
| `permutations_with_replacement` | `multiarrange(n)` | n-permutations with replacement    |

```razorforge
# Clean, math-inspired API
[1, 2, 3].choose(2)        # [[1,2], [1,3], [2,3]]
[1, 2, 3].multichoose(2)   # [[1,1], [1,2], [1,3], [2,2], [2,3], [3,3]]
[1, 2, 3].arrange(2)       # [[1,2], [1,3], [2,1], [2,3], [3,1], [3,2]]
[1, 2, 3].multiarrange(2)  # [[1,1], [1,2], ...all 9 pairs...]
```

**Note:** `pick` reserved for random selection (see Random stdlib below)

### Open Questions

1. **Zipped access:** Indexed (`.0`, `.1`) or named (`.first`, `.second`, `.third`)?
2. **Variadic generics:** Does the compiler support `Zipped<A, B, ...>` with arbitrary arity?
3. **Destructuring:** How does `for (a, b, c) in zipped` work with `Zipped<A, B, C>`?
4. **List overhead:** Is `List<T>` too heavy for small combinations? Use fixed-size array?
5. **Replacement naming:** Which naming scheme for `_with_replacement` operations?

### Related Docs

- [RazorForge SeqTools](https://razorforge.lumi-dev.xyz/SeqTools.md)
- [Suflae SeqTools](https://suflae.lumi-dev.xyz/SeqTools.md)

---

## 10. Random Standard Library

**Status:** ⏳ Open
**Date:** 2025-12-31

### The Question

What should the random number generation and selection API look like?

### Proposed API

**Random number generation:**

```razorforge
# Basic random
Random.integer()                    # Random S64
Random.integer(0 to 100)            # Random S64 in range
Random.float()                      # Random F64 in [0, 1)
Random.float(0.0 to 10.0)           # Random F64 in range
Random.bool()                       # Random Bool
Random.byte()                       # Random U8
Random.bytes(count: 16)             # List<U8> of random bytes
```

**Collection operations:**

```razorforge
# Single selection
list.pick()                         # Random element from list
list.pick_or_default()              # Random element or default if empty

# Multiple selection
list.pick(3)                        # 3 random elements (no replacement)
list.pick(3, replace: true)         # 3 random elements (with replacement)

# Shuffling
list.shuffle()                      # Return new shuffled list
list.shuffle!()                     # Shuffle in place
```

**Weighted selection:**

```razorforge
# Weighted random
choices.pick_weighted(by: c => c.weight)
choices.pick_weighted(3, by: c => c.weight)
```

**Seeded RNG:**

```razorforge
# Reproducible randomness
let rng = Random(seed: 12345)
rng.integer(0 to 100)
list.pick(using: rng)
list.shuffle(using: rng)
```

### Open Questions

1. **Global vs instance:** Should `Random.integer()` use global state or require RNG instance?
2. **Thread safety:** Is global RNG thread-local or shared (with locks)?
3. **Cryptographic:** Separate `SecureRandom` for crypto-safe randomness?
4. **Distribution:** Support for normal, exponential, etc. distributions?
5. **Naming:** `pick` vs `sample` vs `choose` (but `choose` is now combinatorics)
6. **Range syntax:** `Random.integer(0 to 100)` - inclusive or exclusive end?

### Related Docs

- [RazorForge SeqTools](https://razorforge.lumi-dev.xyz/SeqTools.md)
- [Standard Libraries](https://razorforge.lumi-dev.xyz/Standard-Libraries.md)

---

## 11. Module Prelude System

**Status:** ✅ Resolved
**Date:** 2026-01-08

### The Question

Should individual routines/types be automatically available without imports via `@prelude` attribute?

### Design Decision

**Removed `@prelude` attribute. Only `Core` namespace is auto-imported.**

### Before (with `@prelude`)

```razorforge
# In stdlib/Console.rf
@prelude
routine Console.show(value: Text) { ... }

# In user code - works without import
routine start() {
    show("Hello!")  # Where does this come from?
}
```

### After (no `@prelude`)

```razorforge
# RazorForge - must import Console
import Console

routine start() {
    show("Hello!")  # Clearly from Console
}

# Suflae - Console I/O is part of Core (auto-imported)
routine start():
    show("Hello!")  # Part of Core
```

### Resolved Questions

| Issue                         | Decision                                  |
|-------------------------------|-------------------------------------------|
| **`Core` auto-import**        | Kept — fundamental types always available |
| **`@prelude` attribute**      | **Removed** — no magic auto-availability  |
| **Console I/O in RazorForge** | Requires `import Console`                 |
| **Console I/O in Suflae**     | Part of `Core` (auto-imported)            |

### Rationale

- **Explicitness**: Every symbol's origin is clear from imports
- **No magic**: One auto-import (Core), everything else explicit
- **Simpler mental model**: Easier to understand where things come from
- **IDE friendliness**: Auto-import suggestions work naturally

### Related Docs

- [RazorForge Modules and Imports](https://razorforge.lumi-dev.xyz/Modules-and-Imports.md)
- [Suflae Modules and Imports](https://suflae.lumi-dev.xyz/Modules-and-Imports.md)

---

## 12. Membership Operators (`in`, `notin`)

**Status:** ✅ Resolved
**Date:** 2026-01-07

### The Question

How should membership testing work for collections and ranges?

### Design Decision

**`in` operator calls `__contains__` on right operand. `notin` is syntactic sugar for `not (a in b)`.**

```razorforge
# Collection membership
let fruits = ["apple", "banana", "cherry"]
"banana" in fruits      # true
"grape" notin fruits    # true

# Range membership (step-aware)
5 in 0 to 10            # true
10 in 0 to 10           # false (exclusive end)
5 in 0 to 10 by 2       # false (5 not in [0, 2, 4, 6, 8])
4 in 0 to 10 by 2       # true
```

### Resolved Questions

| Issue                  | Decision                                                             |
|------------------------|----------------------------------------------------------------------|
| **Operator semantics** | `a in b` calls `b.__contains__(a)` and returns Bool                  |
| **Negation**           | `notin` keyword is `not (a in b)`, not a separate `__notcontains__`  |
| **Range membership**   | Step-aware O(1) check using remainder: `(value - start) % step == 0` |
| **Range bounds**       | Ranges are exclusive on end: `0 to 10` contains `[0, 1, ..., 9]`     |
| **Downto membership**  | Also step-aware: `(start - value) % step == 0` for descending ranges |

### Range `__contains__` Implementation

```razorforge
routine Range.__contains__(value: S64) -> Bool {
    if me.ascending {
        if value < me.start or value >= me.end {
            return false
        }
        return (value - me.start) % me.step == 0
    } else {
        if value > me.start or value <= me.end {
            return false
        }
        return (me.start - value) % me.step == 0
    }
}
```

### Overloading for Custom Types

```razorforge
entity Set<T> {
    private var elements: List<T>
}

routine Set<T>.__contains__(value: T) -> Bool {
    for elem in me.elements {
        if elem == value {
            return true
        }
    }
    return false
}

let numbers = Set<S32>()
1 in numbers    # Calls Set<S32>.__contains__(1)
```

### Related Docs

- [RazorForge Operators](https://razorforge.lumi-dev.xyz/Operators.md)
- [RazorForge Range Records](https://razorforge.lumi-dev.xyz/Range-Records.md)
- [Suflae Operators](https://suflae.lumi-dev.xyz/Operators.md)
- [Suflae Range Records](https://suflae.lumi-dev.xyz/Range-Records.md)

---

## 13. Variant Semantics

**Status:** Open
**Date:** 2026-01-10

### The Question

What are the exact semantics for user-defined variants?

### Design Decision

**Single-use storage with immediate dismantling.** Variants can be stored once, but must be pattern matched before any
other use.

```razorforge
# ✅ Create and immediately match
when Message.Text("hello"):
    is Text t => process(t)
    is Number n => handle(n)

# ✅ Store once, then dismantle
let msg = Message.Text("hello")
when msg:
    is Text t => process(t)
    is Number n => handle(n)

# ✅ Function returning variant — store or match immediately
let result = parse_input(text)
when result:
    is Success data => use(data)
    is Error e => handle(e)

# ❌ CE: Cannot pass to function
process(msg)

# ❌ CE: Cannot store in collection
list.add_last(Message.Text("hello"))

# ❌ CE: Cannot reassign
msg = Message.Number(42)
```

**If you need to pass around or store in collections, use `Data` instead.**

### Resolved Questions

| Issue                       | Decision                                                    |
|-----------------------------|-------------------------------------------------------------|
| **Internal representation** | `record` (value type, stack allocated)                      |
| **Storage**                 | **Once** — can store in variable, must dismantle before use |
| **Passing**                 | **No** — cannot pass variants as arguments                  |
| **Collections**             | **No** — use `Data` for heterogeneous collections           |
| **Copying**                 | **No** — single owner only                                  |
| **Reassignment**            | **No** — cannot reassign after initial binding              |
| **Generic variants**        | **Yes** — `variant Result<T, E> { Ok: T, Err: E }` allowed  |
| **Nested variants**         | **No** — variant payloads cannot be other variants          |

### What Can Variants Contain?

Variants can hold **any type**, including raw entities.

| Type           | Allow? | Notes                       |
|----------------|--------|-----------------------------|
| Records        | ✅      | Value types                 |
| Choices        | ✅      | Value types                 |
| Shared<T>      | ✅      | Handle (refcount increased) |
| Shared<T, P>   | ✅      | Handle (refcount increased) |
| Actor<T>       | ✅      | Handle (refcount increased) |
| Tracked<T>     | ✅      | Weak ref                    |
| Snatched<T>    | ✅      | Raw pointer (your problem)  |
| Raw entities   | ✅      | Single owner                |
| Other variants | ❌      | No nesting                  |
| Tokens         | ❌      | Scope-bound, can't escape   |

### Variants vs Data

| Need                          | Use       | Why                                                   |
|-------------------------------|-----------|-------------------------------------------------------|
| Immediate type branching      | `variant` | Stack allocated, zero overhead, dismantle on the spot |
| Store/pass heterogeneous data | `Data`    | Heap allocated, refcounted, collection-friendly       |

**Variants** = ephemeral branching construct (like `when` that carries data)
**Data** = persistent universal box (like `Object` in other languages)

These are complementary features, not competing ones.

### Built-in Variants: `Maybe<T>`, `Result<T>`, `Lookup<T>`

**These are NOT normal user-defined variants.** They are compiler primitives with **type-based branch names**:

| Feature                 | User-defined variants                     | `Maybe<T>`, `Result<T>`, `Lookup<T>`        |
|-------------------------|-------------------------------------------|---------------------------------------------|
| Branch names            | Fixed case names (`is Text`, `is Number`) | Type-based (`is Crashable`, `is T`, `else`) |
| Type parameter in match | No — cases are predefined                 | Yes — branch on actual type `T`             |

```razorforge
# User-defined: fixed case names
variant Message { Text: Text, Number: S64 }
when msg:
    is Text t => ...    # "Text" is a fixed case name
    is Number n => ...  # "Number" is a fixed case name

# Built-in: type-based branch names
let result: Result<MyEntity> = try_load_entity()
when result:
    is Crashable e => handle(e)   # "Crashable" is a TYPE, not a case name
    else entity => use(entity)    # Matches the type parameter T

let maybe: Maybe<S32> = try_parse("42")
when maybe:
    is none => show("absent")     # Special "none" case
    else n => show(n)             # Matches S32 (the type parameter)
```

**Why the difference?**

- Built-in variants use the **type parameter** to determine branch semantics
- `Result<T>` matches against `Crashable` protocol (error) vs `T` (success)
- `Maybe<T>` matches against `none` (absent) vs `T` (present)
- `Lookup<T>` matches against `Crashable`, `none`, or `T`
- User variants have statically-defined case names, not type-based matching

**Type inference exclusivity:** Only `Maybe<T>`, `Result<T>`, and `Lookup<T>` support direct type-based matching. This
is a compiler primitive feature — user-defined variants cannot opt into this behavior. They must always use explicit
case names.

### Resolved Questions (Additional)

1. **Variant in entity field?**

   ```razorforge
   entity Container {
       var result: Result<S32>  # ❌ CE: Result<T> cannot be stored in entity field
       var maybe: Maybe<S32>    # ✅ Only Maybe<T> is allowed
   }
   ```

   **Decision:** Only `Maybe<T>` can be stored in entity fields. Other variants (`Result<T>`, `Lookup<T>`, user-defined)
   cannot.

2. **Store in entity field for later dismantling?**

   ```razorforge
   entity Handler {
       var cached: Maybe<S32>   # ✅ Maybe<T> allowed
   }

   handler.cached = try_parse("42")  # ✅ OK

   # Later...
   when handler.cached { ... }       # ✅ OK
   ```

   **Decision:** Only `Maybe<T>` can be stored in entity fields and dismantled later. Other variant types must be
   dismantled immediately.

3. **Case name vs type name clarity**

   ```razorforge
   variant Message {
       TextMsg: Text    # Case name: TextMsg, payload type: Text
       NumMsg: S64      # Case name: NumMsg, payload type: S64
   }

   when msg {
       is TextMsg t => ...  # ✅ Match by case name
       is Text t => ...     # ❌ CE: "Text" is not a case name (but can map to Text)
   }
   ```

   **Decision:** Yes, match by **case name**, not payload type. However, the case name can be mapped to `Text` if
   desired (e.g., `Text` case holds `Text` payload).

4. **Is `none` reserved?**

   ```razorforge
   when maybe {
       is none => ...  # Lowercase 'none' — Maybe<T> literal
       else n => ...
   }
   ```

   **Decision:** Yes, `none` is a reserved literal for `Maybe<T>`. It represents the absent case.

   ```razorforge
   variant MyMaybe<T> {
       none          # ❌ CE: 'none' is reserved for Maybe<T>
       Some: T
   }
   ```

   User-defined variants cannot use `none` as a case name.

### Rationale

- **No hidden costs**: No copy vs move confusion, no expensive implicit operations
- **Explicit**: You see what happens right where it happens
- **Simple**: One rule — create, match, done
- **Escape hatch**: Need storage? Use `Data` instead

### Reference: cve-rs Soundness Hole

The `cvers-src/` directory contains Rust code demonstrating issue #25860:

- `lifetime_expansion.rs` - Core exploit using lifetime translator
- `use_after_free.rs` - Get reference to dropped stack buffer
- Key insight: Complex lifetime inference can have soundness holes

**Lesson for RazorForge:** Keep ownership rules simple and explicit to avoid similar issues.

### Related Docs

- [RazorForge Variants](https://razorforge.lumi-dev.xyz/Variants.md)
- [Suflae Variants](https://suflae.lumi-dev.xyz/Variants.md)

---

## 14. Top Type / Universal Type (`Data`)

**Status:** Open
**Date:** 2026-01-11

### The Question

Should RazorForge/Suflae have a top type (like `Object` in Java/C#, `Any` in Kotlin/TypeScript) that can hold any value?

**Note:** If added to RazorForge, Suflae will definitely have it too.

**Naming decision:** `Data` — simple, doesn't imply inheritance hierarchy, suggests "arbitrary data of any type."

**Implementation:** `Data` is a **type-erased box** — an `entity` that stores any value along with its type information.

- Entities have identity and can be referenced
- Heap allocation is already expected for boxed values
- Reference semantics match how top types work in other languages
- Can participate in `Shared<Data>`, `Tracked<Data>`, etc.

### Motivation

A top type enables:

- Heterogeneous collections without protocols: `List<Data>`
- Dynamic-style programming when needed
- Reflection/introspection APIs
- Generic "box anything" scenarios
- Interop with dynamic languages

### Proposed Implementation

**`Data` is an entity with three fields (24 bytes on 64-bit):**

```razorforge
entity Data {
    type_id: U64      # Identifies the boxed type (for pattern matching, destructor lookup)
    size: U64         # Size of the boxed data in bytes
    data_ptr: UAddr   # Pointer to heap-allocated boxed value
}
```

| Field      | Purpose                                                   |
|------------|-----------------------------------------------------------|
| `type_id`  | Pattern matching, destructor lookup, `to_text()` dispatch |
| `size`     | Know how much data to copy/free, bounds checking          |
| `data_ptr` | Access the actual boxed data                              |

**Memory management:** Standard entity semantics (see [Memory Model](../RazorForge-Wiki/docs/Memory-Model.md)).

**Destructor:** Looked up from `type_id` when `Data` is dropped. No need to store function pointer directly.

### Design Considerations

**Without inheritance, what does "top type" mean?**

In traditional OOP, `Object` is the root of the class hierarchy. Every class IS-A Object. But RazorForge has no
inheritance — entities use `follows` for protocol conformance, not subtyping.

Possible interpretations:

1. **Type-erased box**: `Data` is a fat pointer (data + type ID) that can hold any value
2. **Universal protocol**: `Data` is an empty protocol that everything implicitly follows
3. **Compiler primitive**: Like `Erased<T>` but without requiring a protocol

### Options

| Option                   | Description                                      | Pros               | Cons                                   |
|--------------------------|--------------------------------------------------|--------------------|----------------------------------------|
| **A: No top type**       | Keep current design, use protocols + `Erased<T>` | Simple, explicit   | Can't box arbitrary types              |
| **B: `Any` type**        | Universal box that holds any value               | Flexible, familiar | Type safety concerns, runtime overhead |
| **C: `Dynamic` type**    | Explicit opt-in dynamic typing                   | Clear intent       | Two type systems                       |
| **D: `Tagged<T>` union** | Variant over all primitive types                 | Type-safe          | Only primitives, not custom types      |

### Resolved Questions

1. **Boxing semantics** ✅
    - `Data` is always an entity (heap-allocated)
    - Value types (records, choices): Copied into box
    - Entities: `data_ptr` points to entity

2. **Type recovery** ✅
   ```razorforge
   let box: Data = Data(42)

   # Pattern matching
   when box:
       is S32 n => show(n)
       is Text t => show(t)
       else => show("unknown")

   # Cast (crashes if wrong type) — both syntaxes valid
   let n = S32!(box)      # Constructor style
   let n = box.S32!()     # Method chaining style

   # Safe cast (returns Maybe)
   let n = box.try_S32()  # Maybe<S32>
   ```

3. **Method dispatch** ✅
    - Full reflection-based dispatch
    - Any method callable on `Data`, runtime lookup

4. **Performance** ✅
    - Type ID storage: 8 bytes
    - Heap allocation for boxed value
    - Dynamic dispatch overhead for method calls
    - All costs acknowledged — explicit opt-in

5. **Interaction with existing features** ✅
    - `Data` vs `Erased<Protocol>`: Use `Erased<T>` when you know the protocol, `Data` when truly arbitrary
    - `T: Data` means nothing — not a useful constraint (everything can be boxed)
    - **What `Data` can hold:**

   | Type                         | Can box? | Notes                                   |
   |------------------------------|----------|-----------------------------------------|
   | Records, Choices             | ✅        | Copied into box                         |
   | Primitives (S32, Text, etc.) | ✅        | Copied into box                         |
   | Entities                     | ✅        | `data_ptr` references entity            |
   | Shared<T>, Tracked<T>        | ✅        | Handle copied (refcount++)              |
   | Snatched<T>                  | ✅        | Raw pointer copied (your problem)       |
   | Maybe<T>                     | ✅        | Already storable anywhere               |
   | **Result<T>**                | ❌        | Must dismantle immediately (handle errors) |
   | **Lookup<T>**                | ❌        | Must dismantle immediately (handle errors) |
   | **User-defined variants**    | ❌        | Must dismantle immediately              |
   | **Tokens**                   | ❌        | Scope-bound, cannot escape              |

   **Simple rule:** `Data` holds anything that can already be freely stored/passed. It's not an escape hatch for types that must be dismantled immediately.

   ```razorforge
   # ✅ Maybe<T> is fine
   let box = Data(try_parse("42"))  # Maybe<S32>

   # ❌ Can't box Result/Lookup (must handle errors)
   let box = Data(check_parse("42"))  # CE: Result<S32> cannot be boxed

   # ❌ Can't box variant directly
   let box = Data(Message.Text("hello"))  # CE

   # ✅ Dismantle first, box the payload
   when Message.Text("hello"):
       is Text t => let box = Data(t)
   ```

6. **Type safety** ✅
    - **Decision:** You opted in — you accept the risk.
    - Using `Data` is an explicit choice to trade compile-time guarantees for runtime flexibility.
    - Runtime type errors are possible and expected; handle with pattern matching.

7. **Data inside Data problem** ✅ Resolved
    - **Decision:** Compiler flattening — `Data(Data(42))` becomes `Data(42)`.
    - The compiler automatically unwraps nested `Data` to avoid:
        - Unnecessary indirection and heap allocations
        - Pattern matching confusion
        - Performance overhead

8. **Relationship with variant restrictions (Section 13)** ✅ Clarified
    - `Data` is an **entity** (reference semantics) — cheap to store/pass (pointer copy, refcount++)
    - Variants are **records** (value semantics) — expensive to store/pass (full payload copy)
    - This is not a double standard; the different type categories justify different rules:
        - `Data`: heap-allocated, refcounted → naturally collection-friendly
        - Variants: stack-allocated, value copy → restrictions avoid hidden expensive copies
    - Both share the same pattern-match requirement for type recovery

### Alternative: Reflection API without Top Type

Instead of a top type, provide reflection primitives:

```razorforge
# Type metadata
let type_id = type_of(value)
let name = type_id.name()
let fields = type_id.fields()

# But storage still requires known types or Erased<Protocol>
```

### Related Docs

- [RazorForge Generics](https://razorforge.lumi-dev.xyz/Generics.md)
- [RazorForge Protocols](https://razorforge.lumi-dev.xyz/Protocols.md)
- [Type Erasure (`Erased<T>`)](#4b-type-erasure-erasedt)

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

### Options

[Table or list of options with pros/cons]

### Open Questions

[Specific questions to answer]

### Related Docs

[Links to relevant documentation]
```
