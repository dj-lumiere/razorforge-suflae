# Suflae — Reference for AI Assistants

You are reading this because Suflae (`.sf`, 2026) is **not in your training
data**. Suflae is RazorForge's approachable sibling: same grammar, same standard
library, but a beginner-/scripting-friendly surface that **hides the low-level
machinery RazorForge exposes**. Read `RAZORFORGE-FOR-AI.md` first — everything
there is true for Suflae too **except** where this file overrides it. Do not
guess syntax from Python/Ruby intuitions; this file lists exactly where Suflae
differs from RazorForge.

When unsure, consult ground truth in the repo:

- `tests/Fixtures/StdlibSf/*.sf` — Suflae programs with expected output (the `.sf` cookbook).
- `tests/Fixtures/Stdlib/*.rf` — the RazorForge twins (same behavior, RF surface).
- `Standard/RazorForge/` — the shared standard library (Suflae has **no** stdlib of its own).

---

## 1. What Suflae IS (and how it relates to RazorForge)

- **`.sf` files are analyzed in RazorForge mode over the shared RF `Core`
  stdlib.** There is no separate Suflae standard library — SF consumes RF's
  `Core` directly (`Standard/RazorForge/**`). SF ≡ RF grammar; only the semantic
  lowering differs.
- The compiler selects Suflae by the `.sf` extension. Every `Standard/RazorForge`
  Core type/routine is available. `import IO/Console` + `show(...)` work as in RF.
- SF's identity vs RF: **RF is precision/width-strict; SF is approachable,
  correct-by-default.** The differences below all follow from that.

## 2. The rules most likely to break your assumptions (SF-specific)

1. **`routine start()` is OPTIONAL.** A `.sf` file may consist of loose
   top-level statements; they (plus top-level runtime `var`s) fold into an
   implicit `routine start()`, in source order, with a trailing return. This is
   **script mode** — write a `.sf` like a Python script.
2. **A bare `.sf` file runs like `python hello.py`.** `suflae hello.sf` (or any
   bare `.sf` invocation) builds AND executes the file. (RazorForge is not
   script-mode; a bare `.rf` under `razorforge` dumps the parse instead.)
3. **`module` is optional** — the module path is inferred from the file's
   location (relative to `razorforge.toml`). Declare `module` only to override.
4. **The default number types are `Integer` and `Decimal`, not fixed-width.**
   A bare `42` is an arbitrary-precision `Integer`; a bare `3.14` is a `Decimal`.
   Indices and counts are also `Integer`. (In RF, bare literals default to
   `S64`/`F64`.)
5. **`Decimal` is base-10, not binary.** `0.1 + 0.2 == 0.3` holds in Suflae —
   that footgun-freedom is the whole point. (`Real`, the arbitrary-precision
   *binary* float, is import-only and still can't represent `0.1` exactly.)
6. **`entity` aliases freely — there is NO `steal` in Suflae.** `var b = a` on an
   entity just works (both refer to the same shared thing). Ownership transfer,
   `steal`, and RF-S413 do not exist at the SF surface.
7. **Suflae hides low-level concepts** RazorForge surfaces (see §5). You will not
   write `danger`, `Hijacked`, access tokens (`Viewing`/`Modifying`/`Consulting`/
   `Amending`), `steal`, `@reshaping`, or anything about iterator invalidation.

## 3. Program skeletons

Script mode (statements fold into an implicit `start()`):

```suflae
var total = 0
each n in 1 to 5
  total = total + n
show(f"total = {total}")
```

Explicit `start()` (also fine — most `StdlibSf` fixtures use this form):

```suflae
module Tests/StdlibSf/Demo

routine start()
  show("Hello from Suflae!")
  return
```

- Hoistable declarations (`module`/`import`/`type`/`routine`/`preset`) may
  coexist with loose top-level statements.
- Mixing loose top-level statements AND an explicit `routine start()` in the same
  file is a conflict (SF-G150). Choose one.
- Routines you define are still their own scopes → they still need an explicit
  `return` (the "every scope has one definite exit → teardown anchor" rule). The
  single top-level scope's exit is EOF, so script mode needs no trailing return.

## 4. `entity` = SHARED (don't-copy), backed by `Roamed`

Suflae keeps `record` (value, copied — identical to RF) and `entity`, but
re-grounds the split as **VALUE vs SHARED = copy vs don't-copy**:

```suflae
entity Point          # SHARED: aliases freely, no steal, memory-safe
  x: S64
  y: S64

routine start()
  var p = Point(x: 3, y: 4)
  var q = p                # both refer to the same Point — no steal, no error
  show(f"p: {p}")          # f"{entity}" reads the inner value, prints like the RF twin
  show(f"fields: {p.x}, {p.y}")
  return
```

- Every SF `entity` lowers to **`Roamed[E]`** — RF's biased reference-counting
  handle: cheap same-thread reference count, auto-promote-to-atomic on escape to
  another task, plus a cycle collector (Bacon–Rajan) so reference cycles are
  reclaimed. The user never writes `.roam()`, never annotates weak references,
  never manages lifetimes.
- **Nullable entity: `E?` means a nullable `Roamed[E]`** (a null handle = absent),
  NOT `Maybe[Roamed[E]]`. An entity is already a reference, so absence is the null
  handle (Kotlin `T?` / Rust `Option<Box<T>>` niche). Value types still use
  `Maybe[T]` for `T?`.
  - Dereferencing a nullable `E?` before a null-check is rejected (RF-S619) until
    a check narrows it. Narrow with **capital `None`**: `if n isnot None` /
    `if n is None: return` (guard). Standardize on `None` (the type pattern);
    `isnot none` (lowercase value) does not parse.
  - Constructing/assigning `none` into a non-nullable `E` field is rejected
    (RF-S252) — declare the field `E?` if it may be absent.
- `record` is unchanged from RF: a value type, copied, no identity, no destructor.

## 5. What Suflae deliberately HIDES

A restriction RF surfaces is justified at RF's altitude (close to the metal,
opted-in audience). It does **not** automatically transfer to Suflae. If the only
answer to "why can't I do X" is a machine detail (buffer realloc, dangling
pointer, refcount), the concept must be INVISIBLE in Suflae. Copying an RF
constraint into SF without re-checking its justification is cargo-culting.

Concretely, none of these appear at the SF surface:

- **`steal` / ownership transfer** — entities are shared (`Roamed`); no move.
- **Access tokens** (`Viewing`/`Modifying`/`Consulting`/`Amending`) and the
  `using ... as` lock ceremony — SF reads/writes entities directly.
- **`danger` blocks / `dangerous` routines / `Hijacked`** — SF users cannot reach
  unsafe operations at all (the danger gate rejects `danger!` in SF code). The
  shared stdlib still uses `Hijacked`/`danger` internally — that is fine, because
  the stdlib is always analyzed in RF mode.
- **`@reshaping` / iterator invalidation / `migrate`** — never surfaced. See below.

**Iterate-and-mutate a collection is BLOCKED, not silently reinterpreted.**
Mutating a collection while it is being `each`-looped is banned. At the SF
altitude the explanation is human, not "iterator invalidation": after an
add/remove the loop can no longer trust that its next element is really the next
one. Direct mutation (`each x in xs { xs.add_last(...) }`) is a build-time error
(shared with RF, RF-S625). Indirect mutation (mutation hidden behind a called
routine) is designed to become a runtime `ActiveLoopReshaping` crash (a runtime
"IterGuard" — **designed, not yet built**). The marker that drives this
(`@reshaping`) is RF-facing only; SF users never see it.

## 6. Failure taxonomy (recoverable vs fatal walls)

Suflae has **no try-catch-finally.** Recovery is only for *failable* routines
(`throw`/`absent`) via the generated `try_`/`check_`/`lookup_` variants + `when`,
exactly as in RF. The dividing line:

- **Data condition** (file missing, key absent, parse failure) → the program can
  reasonably respond → **recoverable** (failable `!` + `try_`/`check_`/`lookup_`
  + `when`).
- **Physical/logic limit** (out of memory, runaway recursion) → responding is
  meaningless → **fatal wall**: fails LOUD, in human words, and is NOT catchable.

Fatal-tier details:

- **Out of memory** — fatal, loud, uncatchable (a handler would itself need to
  allocate). SF hides memory *management*, not memory *finiteness*.
- **Deep recursion** — Suflae has NO "stack" in its vocabulary. Frames are memory;
  exhausting them is just the one memory wall (OOM), reported as such. There is no
  depth/watermark concept surfaced.
- **AccessViolation / segfault** — cannot occur in pure SF (memory-safe by
  construction). If one appears, it is a compiler/runtime bug (or unsafe code SF
  called) → report as an internal error, not a language failure mode.
- **External kill (SIGKILL / OOM-killer)** — the process dies before any handler
  runs; SF never gets control and does not narrate it.

Fatal messages name what the PROGRAM did, never the machine (no malloc/OS/signal).

## 7. Number model quick reference

- **Prelude defaults (no import):** `Integer` (arbitrary-precision signed),
  `Decimal` (exact, base-10), `Bool`.
- **Import-only:** `Real`/`Complex` (arbitrary-precision *binary*) via
  `import Numeric/*`; all fixed-width `S8..U256`, `F16..F128`, `D32/D64/D128` via
  `import Core/Numerics/*`.
- Mixing `Integer` ↔ a fixed-width type is an explicit, range-checked (throwing)
  conversion. `//` is floor division, `/` is true division — same as RF.

## 8. CLI

```
suflae hello.sf        # build + run the file (like `python hello.py`)
suflae build hello.sf  # build to a native executable, don't run
```

Configuration lives in `razorforge.toml` (see `RAZORFORGE-FOR-AI.md` §11); there
are no build flags. The shared stdlib resolves relative to the compiler, not the
current directory, so a repo-external `.sf` still finds `Core`.

---

## Status note (do not generate as if shipped)

Suflae is at v0.1. The core loop works end-to-end: entity→`Roamed` lowering,
nullability (`E?` + `None` narrowing + RF-S619 deref guard), single-thread cycle
collection (now multithread-safe via a stop-the-world rwlock), script mode, and
bare-invocation run. **Not yet real:** the runtime `IterGuard` backstop for
indirect loop-mutation, `ObjectHacker` runtime reflection, and hot reload. When
generating Suflae, prefer the closest `tests/Fixtures/StdlibSf/*.sf` fixture,
keep arguments named, and lean on the shared RF Core.
