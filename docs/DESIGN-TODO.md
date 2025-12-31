# Suflae/RazorForge Design TODOs

This document tracks open design questions that need to be resolved.

For resolved decisions, see [DESIGN-DECISIONS.md](archive/DESIGN-DECISIONS.md).

---

## 1. Generator Routines (`generate` keyword)

**Status:** ⏳ Open
**Date:** 2025-12-31

### The Question

How should generator routines work in RazorForge and Suflae?

### Design Decision

**Implicit conversion**: Any routine using `generate` becomes a generator. Return type must be `Sequence<T>`.

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

### Naming

- `Iterator<T>` renamed to `Sequence<T>` (represents a sequence of values)
- `Itertools` renamed to `Seqtools` (methods operating on sequences)

### Additional Open Questions

| Issue                   | Question                                                             | Status |
|-------------------------|----------------------------------------------------------------------|--------|
| **Capture semantics**   | Can generators capture variables like lambdas with `given`?          | ⏳ Open |
| **Delegation**          | Should there be `generate from seq` to delegate to another sequence? | ⏳ Open |
| **Cleanup**             | What happens to generator state when iteration stops early (break)?  | ⏳ Open |
| **Infinite generators** | Are infinite generators allowed? (fibonacci example suggests yes)    | ⏳ Open |
| **Bidirectional**       | Can generators receive values back? (like Python's `send()`)         | ⏳ Open |

#### Capture Example (if allowed)

```razorforge
routine make_counter(start: S32) -> Sequence<S32> {
    var n = start  # Can generator capture/mutate this?
    loop {
        generate n
        n += 1
    }
}
```

#### Delegation Example (if allowed)

```razorforge
routine flatten(lists: List<List<S32>>) -> Sequence<S32> {
    for list in lists {
        generate from list  # Yield all elements from inner list
    }
}
```

### Related Docs

- [RazorForge Routines](../wiki/RazorForge-Routines.md)
- [Suflae Routines](../wiki/Suflae-Routines.md)
- [RazorForge Seqtools](../wiki/RazorForge-Seqtools.md)
- [Suflae Seqtools](../wiki/Suflae-Seqtools.md)

---

## 2. Async Routines and Task Spawning

**Status:** Open
**Date:** 2025-12-30

### The Question

How should async routines (`suspended`), awaiting (`waitfor`), and task spawning (`run_as_task`) work?

### Current Design

```razorforge
suspended routine fetch_data(url: Text) -> Text {
    let response = waitfor http.get(url)
    return response.body
}

suspended routine start() {
    run_as_task(fetch_data("https://example.com"))  # Fire-and-forget
    let data = waitfor fetch_data("https://api.example.com")  # Wait for result
    show(data)
}
```

### The Big Question: Fire-and-Forget Error Handling

**What happens when a `run_as_task` task crashes?**

| Option                    | Behavior                              | Pros                         | Cons                                 |
|---------------------------|---------------------------------------|------------------------------|--------------------------------------|
| **A. Crash program**      | One task failure kills all            | Simple, errors can't hide    | Too brutal for production            |
| **B. Isolated + logged**  | Task crashes logged, others continue  | Simple, resilient            | Errors may go unnoticed              |
| **C. Linked tasks**       | Caller + task crash together          | Predictable                  | Fire-and-forget still crashes caller |
| **D. Supervisor pattern** | Parent decides: restart/stop/escalate | Most flexible, battle-tested | Complex to implement                 |

**Suggested:** Default isolated + logged, with `run_as_linked_task` for strict mode.

### Additional Open Questions

| Issue                 | Question                                                                              |
|-----------------------|---------------------------------------------------------------------------------------|
| **Return value**      | `run_as_task` is fire-and-forget. Should there be `spawn_task` that returns a handle? |
| **Cancellation**      | How to cancel a running task? Need `TaskHandle` with `.cancel()` method?              |
| **Task limits**       | What if thousands of tasks spawned? Backpressure mechanism?                           |
| **Function coloring** | `suspended` creates async/sync divide. Accepted tradeoff?                             |
| **Suflae actors**     | How does `suspended`/`waitfor` interact with actor model?                             |

### Related Docs

- [RazorForge Routines](../wiki/RazorForge-Routines.md)
- [Suflae Concurrency Model](../wiki/Suflae-Concurrency-Model.md)

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

- [RazorForge Routines](../wiki/RazorForge-Routines.md)
- [Suflae Routines](../wiki/Suflae-Routines.md)
- [RazorForge Seqtools](../wiki/RazorForge-Seqtools.md)

---

## 4. Generic Variance

**Status:** Open
**Date:** 2025-12-31

### The Question

Should generic types support variance (covariance/contravariance)?

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

### Recommendation

Start with **Option A (Invariant only)** for simplicity. Add variance later if needed.

### Related Docs

- [RazorForge Generics](../wiki/RazorForge-Generics.md)
- [RazorForge Protocols](../wiki/RazorForge-Protocols.md)

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
    list.push(1)
    list.push(2)

    verify!(list.length() == 2)
    verify!(list.get!(0) == 1)
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

- [Standard Libraries](../wiki/Standard-Libraries.md)

---

## 6. String Interpolation Expression Limits

**Status:** Open
**Date:** 2025-12-31

### The Question

What expressions are allowed inside f-string interpolation `{}`?

### Options

| Option                    | Allowed Expressions                         | Pros                | Cons                             |
|---------------------------|---------------------------------------------|---------------------|----------------------------------|
| **A. Any expression**     | Identifiers, operators, calls, conditionals | Maximum flexibility | Complex parsing, potential abuse |
| **B. Simple expressions** | Identifiers, field access, method calls     | Readable            | Limited                          |
| **C. Identifiers only**   | Just `{x}`, no `{x.y}` or `{foo()}`         | Simplest            | Too restrictive                  |

### Expression Examples

```razorforge
f"{name}"                # Level 1: Identifiers - OK
f"{user.name}"           # Level 2: Member access - OK?
f"{list.length()}"       # Level 3: Method calls - OK?
f"{a + b}"               # Level 4: Operators - OK?
f"{if x > 0 then "+" else "-"}"  # Level 5: Complex - OK?
```

### Recommendation

**Level 3 (method calls)** - identifiers, field access, method calls allowed; operators and complex expressions require
temp variables.

### Related Docs

- [RazorForge Text](../wiki/RazorForge-Text.md)
- [COMPILER-TODO](COMPILER-TODO.md)

---

## 7. Result/Lookup Unwrapping Ergonomics

**Status:** Open
**Date:** 2025-12-31

### The Question

Is the current `when` pattern for unwrapping `Result<T>` too verbose for common cases?

### The Problem

```suflae
# Current pattern - verbose
let num = when check_parse(input):
    is Crashable e:
        alert(e.crash_message())
        0
    else v => v
```

### Possible Solutions

**Option A: Method-based unwrapping**

```suflae
let num = check_parse(input).or_default(0)
let num = check_parse(input).or_else(handle_error)
```

**Option B: Operator-based**

```suflae
let num = check_parse(input) ?? 0
```

**Complication:** `Lookup<T>` has three cases (Error | None | Value) - operators become ambiguous.

### The "Trailing Value" Problem

```suflae
is Crashable e:
    alert(f"Error: {e.crash_message()}")
    0  # Looks like a stray value, not intentional return
```

Possible solutions: `then` keyword, require `return`, or discourage the pattern.

### Open Questions

1. Should we add `.or_default()` / `.or_else()` methods to Result/Lookup?
2. Should there be an operator like `??` for Result unwrapping?
3. How to handle "do something AND return a value" in Suflae's indentation syntax?

### Related Docs

- [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md)
- [Suflae Error Handling](../wiki/Suflae-Error-Handling.md)

---

## 8. Suflae Actor Field Access for Seqtools

**Status:** Open
**Date:** 2025-12-31

### The Question

How should Suflae's `Shared<T>` (actors) interact with itertools operations?

### The Problem

In Suflae, `Shared<T>` creates an **actor** - a thread-safe entity with isolated internal state accessible only via
message passing. This means you can't directly apply itertools to actor fields:

```suflae
entity DataHolder:
    var items: List<Integer>

let holder = DataHolder(items: [1, 2, 3, 4, 5]).share()

# Cannot access fields on Shared<T> (actor)
# holder.items.where(x => x > 2)  # ERROR: Can't access fields on actors
```

Currently, the only way is to define methods on the entity that perform itertools operations internally:

```suflae
routine DataHolder.get_filtered(pred: (Integer) -> Bool) -> List<Integer>:
    return me.items.where(pred).to_list()

# Then:
let filtered = holder.get_filtered(x => x > 2)
```

This is verbose and requires defining methods for every itertools operation you might need.

### Options

| Option                                   | Behavior                                          | Pros                              | Cons                                                            |
|------------------------------------------|---------------------------------------------------|-----------------------------------|-----------------------------------------------------------------|
| **A. `.ask()` helper**                   | `holder.ask(h => h.items.where(...).to_list())`   | Explicit, safe                    | Still verbose, closure syntax                                   |
| **B. Read-only field access (snapshot)** | `holder.items` returns a snapshot `List<Integer>` | Most intuitive, familiar syntax   | Hidden copy (performance?), might mislead users about isolation |
| **C. Get snapshot first**                | `let snapshot = holder.snapshot(); ...`           | Explicit about copying            | Extra step, verbose                                             |
| **D. Don't mix actors with itertools**   | Require explicit design patterns                  | Clear separation, forces good API | Limits convenience, steep learning curve                        |

### Leaning Toward: Option B

**Proposed behavior:** Read-only field access on `Shared<T>` returns a **snapshot** (copy) of the field value:

```suflae
let holder = DataHolder(items: [1, 2, 3, 4, 5]).share()

# Reading 'items' returns a snapshot (copy)
let result = holder.items.where(x => x > 2).to_list()

# Modifications to the copy don't affect the actor
let copy = holder.items
copy.push(100)  # Only affects the copy, not the actor
```

### Open Questions

1. **Performance:** Is the implicit copy acceptable? Should there be a warning/lint?
2. **Mutability:** What about writing? `holder.items.push(100)` - should this be an error?
3. **Deep copy:** For nested entities, is it a shallow or deep copy?
4. **Consistency:** Does this break the actor isolation model conceptually?
5. **Alternatives:** Should there be `.ask()` for complex operations that need atomicity?

### Related Docs

- [Suflae Concurrency Model](../wiki/Suflae-Concurrency-Model.md)
- [Suflae Seqtools](../wiki/Suflae-Seqtools.md)

---

## 9. Zip Return Type

**Status:** ⏳ Open
**Date:** 2025-12-31

### The Question

What type does `zip` return when combining two sequences?

### Current Docs

The Seqtools docs say:
```razorforge
seq1.zip(with: seq2)  # Combine parallel elements into records
```

But what exactly is that "record"?

### Options

| Option | Return Type | Example | Pros | Cons |
|--------|-------------|---------|------|------|
| **A. Anonymous record** | `{ first: A, second: B }` | `for pair in a.zip(with: b) { pair.first + pair.second }` | Clear field names | Verbose access |
| **B. Pair<A, B> type** | `Pair<A, B>` | `for pair in a.zip(with: b) { pair.a + pair.b }` | Explicit type | Another stdlib type |
| **C. Destructuring only** | Unnamed pair | `for (x, y) in a.zip(with: b) { x + y }` | Clean syntax | Can't access without destructuring |
| **D. Indexed-style** | `Zipped<A, B>` with `.left`/`.right` | `for z in a.zip(with: b) { z.left + z.right }` | Consistent with `Indexed<T>` | New type needed |

### Multi-Zip

What about `zip` with 3+ sequences?

```razorforge
seq1.zip(with: seq2, with: seq3)  # Three-way zip - what type?
```

### Cartesian Product

Same question for `product`:

```razorforge
seq1.product(with: seq2)              # All combinations (a1,b1), (a1,b2), ... - what type?
seq1.product(with: seq2, with: seq3)  # Three-way product - what type?
```

### Related to Combinatorics

Similar question for `combinations` and `permutations`:

```razorforge
[1, 2, 3].combinations(pick: 2)  # Returns Sequence<???> - pairs of what type?
[1, 2, 3].permutations(pick: 2)  # Same question
```

### Open Questions

1. Should `zip`/`product` return a named type (`Pair`, `Zipped`) or anonymous record?
2. How does destructuring work with the chosen type?
3. Should multi-way operations (3+ sequences) be supported? What type?
4. Same question applies to `combinations`/`permutations` - what's the element type?
5. Should all these operations share the same return type pattern for consistency?

### Related Docs

- [RazorForge Seqtools](../wiki/RazorForge-Seqtools.md)
- [Suflae Seqtools](../wiki/Suflae-Seqtools.md)

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
