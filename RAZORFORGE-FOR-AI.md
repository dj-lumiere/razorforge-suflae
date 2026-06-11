# RazorForge — Reference for AI Assistants

You are reading this because RazorForge (v0.0.1-alpha, 2026) is **not in your
training data**. Do not guess syntax from Rust/Swift/Python intuitions — this
file lists exactly where those intuitions are wrong. Everything here is taken
from programs that compile and run in CI.

When unsure, consult ground truth in the repo/package:

- `tests/Fixtures/Stdlib/*.rf` — 90+ complete programs with expected output (the cookbook).
- `Standard/RazorForge/` — the standard library sources (`.rf`), including every public API.

---

## 1. The rules most likely to break your assumptions

1. **Multi-parameter calls REQUIRE named arguments.**
   `gcd(252, 105)` ✗ → `gcd(a: 252, b: 105)` ✓
   Single-argument calls may be positional. A few stdlib routines annotated
   `@positional` (e.g. `x.clamp(0, 100)`) accept all-positional; mixing named
   and positional in one call is always an error (RF-S512).
2. **Normal routine completion uses an explicit `return`** — even void ones.
   This is load-bearing (destructor scheduling anchors at `return`), not style.
   Failable routines may also terminate with `throw` or `absent`.
3. **Conditional expressions are top-level only.**
   `return if c then a else b` ✓ — but `f(x: if c then a else b)` or
   `(if c then a else b) + 1` are parse errors *by design*. Use `when` or a
   named intermediate instead.
4. **`//` is integer floor division; `/` is true division.**
5. **Default arithmetic is checked** — `+ - *` throw on overflow. Wrapping
   variants: `+% -% *%`. Clamping (saturating): `+^ -^ *^`.
6. **Entities have a single owner.** Assigning or passing an entity requires
   explicit transfer: `consume(r: steal b)`. Plain `var s = obj.field_entity`
   is rejected (RF-S413).
7. **Failable routines carry a `!` suffix and failure handling is enforced.**
   You either call from another failable routine, match with `when`, or call a
   compiler-generated variant. `try_foo` is always generated (→ Maybe);
   `check_foo` is generated for routines that can throw (→ Result);
   `lookup_foo` is generated when a routine can both throw and be absent
   (→ Lookup). There are no exceptions in the Java/C# sense.
8. **Bare integer literals adapt to context; variables do not.**
   `h << 5` and `x.clamp(0, 100)` are fine (literals conform), but mixing a
   `U64` variable with an `S64` variable needs explicit conversion.
   Out-of-range literals are compile errors (RF-S010): `-1` never fits an
   unsigned type — spell all-ones as `U8_MAX`, `U64_MAX`, etc.
9. **Ignored Bool returns need `discard`**: `discard seen.add(value: v)` (RF-W007).
10. **Indentation is 2 spaces and blocks are indentation-delimited.** No braces.
11. **`?T` (prefix) is an in-flight type** (entity being constructed/transferred);
    **`T?` (postfix) is Maybe**. They are different things — never conflate.
12. **Chained comparisons are one expression**: `0 <= x <= 10` works.
13. **Printing is `show(...)`** (after `import IO/Console`), not print/println.
14. **The entry point is `routine start()`**, not `main`.

## 2. Program skeleton

```razorforge
module My/Module/Path
import IO/Console

routine start()
  show("Hello from RazorForge!")
  return
```

Every file begins with `module <Path>`. Imports use `/` separators.

## 3. Types

- Signed ints: `S8 S16 S32 S64 S128` · unsigned: `U8 U16 U32 U64 U128`
- Floats: `F16 F32 F64 F128` · decimals: `D32 D64 D128`
- Arbitrary precision: `Integer` (literal suffix `n`), `Decimal` (`dn`)
- Complex: `j32/j64/j128/jn` literal suffixes (e.g. `3j64`)
- `Bool`, `Text` (UTF-32 string), `Character`, `Byte`, `Bytes`
- `Duration`, `ByteSize` (with literal forms), `Moment`/`LocalMoment` temporals
- Collections: `List[T]`, `Dict[K,V]`, `Set[T]`, `Deque[T]`, `BitList`, `PriorityQueue[TPriority, TElement]`,
  `SortedDict[K, V]`, `SortedList[T]`, `SortedSet[T]`, fixed-size `Array[T, N]`, `BitArray[N]`
- Tuples: `(T, U)` / `Tuple[T, U]`, with fields `item0`, `item1`, ...
- Carriers: `Maybe[T]`, `Result[T]`, `Lookup[T]`
- Typed literal suffixes exist: `7s32`, `0_s64`, `1.5f32` (underscore optional)

## 4. Variables and operators

```razorforge
var count = 0_s64        # variable binding (the everyday declaration)
count = count + 1        # bare literal 1 conforms to S64

preset MAX_RETRIES: S32 = 5   # named constant: UPPER_CASE, explicit type required
```

There is **no `let` and no `const`** — `var` for bindings, `preset` for
constants. (`lateinit`/`uninit` exist for deferred initialization; see stdlib
usage before reaching for them.)

- Checked: `+ - *` (throw on overflow) · wrapping: `+% -% *%` · clamping: `+^ -^ *^`
- Shifts: `<<` `>>` arithmetic, `<<<` `>>>` logical; shift amounts are `U32`
  (bare literal amounts fine)
- `//` floor division, `/` true division, `%` remainder
- `abs()` on signed ints is failable (`abs!()` throws on MIN); the force-unwrap
  idiom is `x.try_abs()!!`

## 5. Control flow

```razorforge
if x == 3
  show("three")

for x in 1 to 5          # range iteration
  if x == 3
    continue
  if x > 4
    break
  show(f"x={x}")

while i < 10_s64
  i = i + 1_s64

loop                      # infinite loop, exit with break
  n = n + 1
  if n >= 3
    break
```

`when` is the pattern match:

```razorforge
when m
  is None => show("absent")
  else p  => show(f"present: x={p.x}")   # else with binding

when n
  == 0 => throw DivisionByZeroError()
  == 1 => return "one"
  else => return "many"

when v
  is Crashable e => show("failed")
  is S64 x       => show(f"ok: {x}")     # type pattern with binding
```

## 6. Routines

```razorforge
routine add(a: S64, b: S64) -> S64
  return a + b

routine Point.magnitude() -> F64        # method: Type.name, receiver is `me`
  return F64(me.x * me.x + me.y * me.y).sqrt()

routine get_text!(n: S64) -> Text      # `!` = failable
  when n
    == 0 => throw DivisionByZeroError()
    else => return "ok"

dangerous routine raw_poke(p: Address)  # callable only inside danger! blocks
  ...
  return
```

- Call with named args: `add(a: 1, b: 2)`. The compiler generates
  `try_get_text(n: 0)` → `Maybe[Text]`/`Text?` automatically. If the routine
  can throw, it also generates `check_...`; if it can both throw and be absent,
  it generates `lookup_...`.
- `$`-prefixed routines are lifecycle/operator hooks: `$create`, `$destroy`,
  `$copy`, `$eq`, `$cmp`, `$represent` (to-text), `$diagnose` (debug text),
  `$getitem!`/`$setitem` (indexing), `$iter`/`$next` (iteration), `$add` etc.
  (operator overloads).
- Failure inside a failable routine: `throw SomeError(...)` or `absent`
  (absence without an error object).
- Bare routine names are first-class values: `select(transform: double)`
  (free routines only).

## 7. Records vs entities (the memory model)

```razorforge
record Point          # VALUE type: copied, no identity, no destructor
  x: S64
  y: S64
# construct memberwise, strictly named:
var p = Point(x: 3, y: 4)

entity Resource       # HEAP type: single owner, deterministic $destroy
  tag: S64

var b = Resource(tag: 7s32)
consume(r: steal b)   # ownership moves; using b afterwards = compile error
# $destroy runs exactly once, at the owner's scope exit (anchored at `return`)
```

- Containment is ownership: one owner at a time; `steal` marks every transfer.
- Returning a bare entity transfers ownership to the caller.
- Scoped borrows: `view` (read intent) / `modify` (write intent) are
  scope-bound — they cannot be returned or stored, and you should not bind them
  with `var`. Inline `item.view()` / `item.modify()` is fine for a single call;
  use `using item.view() as v` / `using item.modify() as m` when a borrow needs
  a name or spans multiple statements. To lend storably without ownership, use `Hijacked[T]`
  (non-owning handle, no-op destroy). Shared ownership is opt-in via
  `Retained[T]` / `Tracked[T]` (reference counting).
- `Retained[T]` is different from `Viewing[T]`/`Modifying[T]`: it is storable,
  and it forwards direct access to the retained entity (`r.payload`,
  `r.method(...)`). Copying/sharing a retained handle must be explicit via
  `.retain()`; weak handles use `.track()`.
- Records never use `view`/`modify`/`as_entity` — those are entity concepts.
- Unsafe operations live in `danger!` blocks; `dangerous` routines can only be
  called inside them.
- There is **no borrow checker** and no lifetime syntax; safety comes from
  single ownership + marked transfers.

## 8. Generics and protocols

```razorforge
routine largest[T obeys Comparable](items: Viewing[List[T]]) -> T
  ...

record Pair[A, B]
  first: A
  second: B

protocol Iterable[T]
relates Iter obeys Iterator[T]        # associated type slot
  routine $iter() -> Me/Iter          # `/` projects an associated type

entity List[T] obeys Iterable[T]
relates ListEmitter[T] as Iter        # associated type binding
```

- Constraint syntax: `T obeys SomeProtocol`. `Me` is the self type.
- Const generics: `Array[T, N]`.
- Everything monomorphizes; there is **no runtime dispatch** of any kind.

## 9. Text and formatting

```razorforge
show(f"x={x} and pi≈{pi}")    # f-string interpolation
show(f"debug: {value:?}")     # :? = diagnose (debug) format spec
```

`Text` is UTF-32, a record (value type). `Bytes` is a record for raw byte
sequences with UTF-8 iteration helpers. A `b'x'` byte-letter literal has type
`Byte`; `b"..."` has type `Bytes`.

## 10. Collections quick reference

Verify exact signatures in `Standard/RazorForge/Collections/` — highlights
that differ from other languages:

- `list.add_last(value: v)`, `list.remove_first!()`, `list.remove_at!(0)` —
  removal is failable (empty/out-of-range throws); `try_remove_first()` returns
  Maybe.
- `set.add(value: v)` returns Bool — `discard` it if unused.
- `dict.add(key: k, value: v)` returns Bool; indexing is failable under the
  hood, so use `dict.try_getitem(key: k)` when you want `Maybe[V]`.
- Indexing `coll[i]` is failable under the hood (`$getitem!`); back-indexing
  is `coll[^1]` (last element).
- `List[T]`, `Dict[K, V]`, `Set[T]`, `Deque[T]`, and sorted collections are
  entities. Do not pass a container as a bare parameter when read-only access is
  enough; use `Viewing[List[T]]` and pass `items.view()` inline for one call.
  Use `using items.view() as v` only when the borrow needs a name or spans
  multiple statements. Do not write `var v = items.view()`.
- `SortedList`/`SortedSet` have **no positional indexing** — rank access is
  `get_by_rank!(...)`.
- Iterator adapters (from `IterTools`): `select`, `where`, `zip`, `enumerate`,
  `chain`, `distinct`, `select_many`, `min_by`, … — lazily evaluated, chainable,
  lambdas like `x => x % 2 == 0`.
- Ranges: `1 to 5` inclusive, `1 til 5` exclusive, optional step with `by`
  (e.g. `1 to 10 by 2`).

## 11. CLI and project manifest

```
razorforge buildandrun hello.rf     # build + link + execute one file
razorforge build [entry] [out.ll]   # multi-file build
razorforge check [entry]            # type-check only
razorforge parse|tokenize <file>    # front-end inspection
razorforge version
```

**There are no build flags.** All configuration lives in `razorforge.toml`:

```toml
[package]
name = "my-app"

[target]
executable = "MainModule"   # entry MODULE name (the `module` decl, not a file path)
library = ["../shared"]     # external dependency DIRECTORIES (optional)
mode = "debug"              # debug -O0 | release -O2 | release-time -O3 | release-space -Os
dump-ast = false            # optional: write .rf.desugared beside generated IR
sa-timing = false           # optional: print semantic-analysis phase timings
show-build-stages = false   # optional: print build/check stage banners
```

With no entry file argument, the CLI walks up from the cwd to find
`razorforge.toml` — `cd` into a project and `razorforge buildandrun` works.

## 12. Reading compiler errors

Format: `error[RF-S###]: file:line:col: message` plus a source excerpt with a
caret. Families: `RF-G###` grammar/parse, `RF-S###` semantic, `RF-W###`
warning. Frequent ones when porting habits from other languages:

| Code         | Usual cause                           | Fix                                                |
|--------------|---------------------------------------|----------------------------------------------------|
| RF-S510/S512 | positional args in a multi-param call | name every argument                                |
| RF-S413      | entity assigned without transfer      | add `steal`                                        |
| RF-S753      | failable call left unhandled          | use `try_`/`check_`/`lookup_` variant or `when`    |
| RF-S010      | literal out of range for target type  | use the right constant (`U8_MAX`)                  |
| RF-W007      | ignored Bool return                   | `discard expr`                                     |
| RF-G055/G112 | brace-style or inline-`if` habits     | 2-space indent blocks; conditionals top-level only |

## 13. A complete worked example

```razorforge
module Demo/Inventory
import IO/Console

record Item
  name: Text
  qty: S64

routine find_qty!(items: Viewing[List[Item]], name: Text) -> S64
  for item in items
    if item.name == name
      return item.qty
  absent

routine start()
  var items = List[Item]()
  items.add_last(value: Item(name: "bolt", qty: 40))
  items.add_last(value: Item(name: "nut", qty: 0))

  var q = try_find_qty(items: items.view(), name: "bolt")
  when q
    is None => show("not found")
    else n  => show(f"bolt qty: {n}")

  show("DONE")
  return
```

When generating RazorForge: start from the closest fixture in
`tests/Fixtures/Stdlib/`, keep arguments named, end every routine with
`return`, and prefer `when` over nested conditionals.
