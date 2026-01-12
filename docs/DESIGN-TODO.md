# Suflae/RazorForge Design TODOs

This document tracks open design questions that need to be resolved.

For resolved decisions, see [DESIGN-DECISIONS.md](archive/DESIGN-DECISIONS.md).

---

## 1. Testing Framework

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

## 2. Zip Return Type

**Status:** Open
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

## 3. Random Standard Library

**Status:** Open
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