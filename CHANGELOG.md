# Changelog

All notable changes to RazorForge and Suflae are documented here. This project follows
[Keep a Changelog](https://keepachangelog.com/) conventions; RazorForge and Suflae version
independently (`RazorForgeVersion` / `SuflaeVersion` in the project manifest).

## [Unreleased] — RazorForge 0.4.0 · Suflae 0.1.0

_Covers everything since `v0.3.0` (234 commits). RazorForge 0.4 is largely an API-surface cleanup
release — read the **Breaking Changes** section before upgrading._

### ⭐ Suflae 0.1.0 — the first Suflae release

Suflae (`.sf`) is now a working language sharing RazorForge's grammar and stdlib, with its own
ownership runtime and an approachable surface. It ships alongside RazorForge 0.4 via the `suflae` /
`sf` CLI.

- **Realm-scoped Core.** RazorForge and Suflae each have their own `module Core`, distinguished by
  **realm**: a `.sf` file gets the Suflae-realm Core analyzed in SF mode, a `.rf` file the
  RazorForge-realm Core in RF mode; both coexist in one compilation. Cross-realm references bridge via
  `RF::` / `SF::` (e.g. `RF::Core.List`).
- **Entities are `Roamed[T]`.** An SF `entity` is a biased-refcounted, cycle-collected handle
  (auto-promote-on-escape + task-keyed lock). Recursive lowering handles nested entities, entity
  fields, params/returns, and nullability (`E?` = a nullable `Roamed[E]`, no double-`Maybe`).
- **SF-realm Core stdlib** under `Standard/Suflae/`: entity collection wrappers (List, Dict, Set,
  Deque, PriorityQueue, Sorted{List,Dict,Set}, SplitList, BitList) delegating to `RF::Core` via an
  auto-forwarding pass; value types (Text/Bytes/Integer/Real/…) are shared, not wrapped.
- **Approachable surface:** `Integer`/`Decimal` are the default number types; fixed-width types
  (`S32`/`U64`/`F128`/…) and their literal suffixes are import-gated behind `import Numerics`
  (**RF-S636**); `@dangerous`/unsafe free-routine calls are rejected (**RF-S800**).
- **Script mode.** Top-level statements and `var` decls in a `.sf` file fold into an implicit
  `start()`; running a `.sf` directly builds and executes the file, `python hello.py`-style.
- **Concurrency:** SF `suspended` routines are enabled (lazy Agent model, below).
- **Testing:** a `.sf` fixture harness with an RF↔SF equivalence lock (shared fixtures must produce
  byte-identical output) runs in the main test suite.

### ⚠️ Breaking Changes (RazorForge)

**Renamed types, protocols & terminology**
- RC wrappers: `Shared` → **`Guarded`**, `Watched` → **`Witnessed`**; controllers `ShareController` →
  **`GuardController`**, `ImmutableSharedController` → **`FrozenController`** (Retained/Tracked/Roamed
  unchanged).
- Access tokens: `Inspecting` → **`Consulting`**, `Claiming` → **`Amending`**, `Referring` →
  **`Accessing`** (and the read-only coercion `refer()` → **`access()`**, symmetric with
  `Controlling`/`control()`).
- Capability protocol `Storable` → **`Assignable`**; reflection vocabulary `Field` →
  **`MemberVariable`**, `Method` → **`MemberRoutine`**.
- Annotation `@migratable` → **`@reshaping`**.
- Buildtime reflection: keyword `armof` → **`branchof`**; `memvarof` split into **`openmemvarof`** /
  **`allmemvarof`**; `expand` is now the only reflection keyword (sources/accessors are
  `BuilderExpansion`-gated intrinsics).

**Reference-counting surface**
- **Construction verbs abolished.** `T.roam()`, `T.retain()`, and `T.share[P]()` (entity→wrapper
  "constructors" masquerading as verbs) are removed — construct a wrapper with
  `Wrapper(from: steal n)` (e.g. `Roamed(from: n)`, `Retained(from: steal x)`).
- Copy/lifecycle verbs renamed to a canonical vocabulary: RC copy verb `store` → **`share`**;
  conversion verbs `track`/`watch` → **`observe`**, `recover` → **`hold`**; controller counts
  `increase/decrease_strong_count` → **`hold`/`unhold`**, weak → **`observe`/`unobserve`**.
- The public `release()` verb is removed from all RC wrappers — `destroy()` **is** the decrement
  (canonical surface: `share` / `observe` / `hold` / `access` / `control` / `destroy`).

**Removed features**
- `Representable` / `Diagnosable` / `Serializable` protocols removed — `represent` / `diagnose` /
  `serialize` are now **universal built-ins**, not opt-in capabilities (declare with `override`).
- `FastDict` / `FastSet` removed from the collections module.
- The `Blank` unit type is replaced by **`None`**.
- The `$` wired-sigil surface is removed (wired-ness is inferred from protocol conformance); the dead
  `?T` prefix concept is gone.

**Changed semantics**
- **`Copyable` is decoupled from `Storable`/`Assignable`** into orthogonal capability axes; `Copyable`
  auto-derives via a `needs P everywhere` gate; RC wrappers no longer imply it (a duplicate is an
  explicit `.share`).
- Failability: the `!` marker is no longer part of a routine's name — failability is an attribute
  (`IsFailable`), and `foo` / `foo!` are the same routine.
- Three-rules parameter model: record params borrow, `retain` moves to the destination
  (constructor / store-primitive), the caller tears down rvalue args.

### Added (RazorForge)

**Language surface**
- `text[a til b]` range/slice indexing via `getitem(range: Range[U64])`; `Range[T]` is a record with a
  `RangeEmittable[T]` iterator, and range/slice indexing works across collections.
- Single-hole `_` lambda sugar; named-argument punning (field-init shorthand).
- SoA collections `SplitList` / `SplitArray`; `is <Name>Type` compiler-group constraints.
- Zero-initialization routines `blank()` / `hollow()`.
- Carrier lowering: `Try`/`Check`/`Lookup` failable variants lower to `Maybe`/`Result`/`Lookup`
  values (the internal `#carrier` representation is eliminated program-wide; payload is a single
  `CPtr` slot).

**Numerics**
- Quaternions `Q32` / `Q64`; vector types `Vector2D` / `Vector3D` / `Vector4D`.
- Native SIMD `Vector[T, N]` with elementwise arithmetic and reductions.

**Concurrency**
- **Lazy Agent model.** A `suspended` call builds an inert coroutine that a verb starts: `retrieve`
  awaits one, `gather`/`race` launch a set, `execute` detaches for fire-and-forget. **RF-W008** warns
  on a never-launched agent.

**Buildtime & reflection**
- `expand` / `${…}` reflection: `openmemvarof` / `allmemvarof`, `branchof` / `caseof`, and `*of`
  metadata intrinsics (`nameof`/`orderof`/`typeof`/`typeidof`/`valueof`/`placeof`/`sizeof`/
  `visibilityof`) with repr-C offset/size folding.
- `serialize` universalized; derive classification is opt-in and constraint-driven
  (`needs P everywhere` / `obeys P`). Buildtime-value const-generics (`${…}`).

**Build & FFI**
- File-granularity conditional compilation via the `@target` annotation (parsed before `module`, read
  pre-parse, editor-highlightable); `@target` supports multi-value keys (`os: "linux", "macos"`) and
  gates stdlib file loading.
- Link external C libraries via `[target] c_libraries` / `library_paths` (clang `-l`/`-L`) and the
  `@link("lib")` source annotation on `C::` externs.
- Platform-width C FFI types: `CLong` / `CULong` / `CWChar` (per-target) and `CWStr` (`wchar_t*`).
- Float-struct ABI (Phase 3): per-eightbyte SSE/INTEGER (SysV x86-64), HFA (AAPCS64), GP-reg-by-size
  (Win-x64).
- `RoutineRealm {RF, SF, C, LLVM}`; foreign calls migrated from `external("C"|"llvm")` to
  realm-qualified `C::` / `LLVM::` routines (strict realm enforced at call sites — **RF-S460**).
- Versions sourced from the csproj `AssemblyMetadata` via `BuildInfo`; `builder_version()` is
  language-specific.

### Changed (non-breaking)

- **Object-level liveness on `RoamController`:** weak observation + tombstone (a `Watching` handle,
  `is_alive()` / `is_destroyed()`), cycle-collect and multithread safe.
- Opt-in escaped-lock **deadlock detector** (`RF_DEADLOCK_DETECT`): a contended `Roamed` lock cycle
  aborts loudly with the offending task ids instead of hanging.
- `RangeState`/cycle-collector renamed to meaning-based `RoamState {LIVE/SCANNING/GARBAGE/SUSPECTED}`;
  internal `cc_` prefix → `cyclic_`.
- New diagnostics: **RF-S622** (aggregate-steal holes `steal l[i]` / `steal o.field`), **RF-S262**
  (ambiguous module-scoped type reference); keeping a store-less value read from a container/aggregate
  is rejected.
- `RuntimeContract` is the single source of truth for `represent`/`diagnose`/`serialize` and
  wrapper-name literals. `dump-ast` reworked to capture the exact pre-LLVM-codegen AST.
- Name canonicalization: routine names are structurally bare (owner / member / receiver / generic
  args are separate structured fields, not parsed from the name string).

### Fixed

- **Cycle-collector UAF** under concurrency (rwlock stop-the-world) + stack-overflow
  (recursion → explicit worklist); roam-trace now descends into open-addressing (Dict/Set) and
  bare-entity container fields so elements are collectable.
- **Agent double-free** — task lifetime inverted to a worker↔`retrieve` self-reap rendezvous.
- Several **over-prune / heap-corruption crashes** from user types whose names collided with a generic
  parameter (`record T`/`U`/`N`): generic-parameter identity is now keyed by positional slot, not name
  (channel-buffer under-allocation, `pick!` param mismatch, derive-template shadowing, cross-module
  signature-cache contamination — all fixed with regression fixtures).
- **Teardown correctness:** emit destroys *before* a terminator (spilling return/throw values to a
  temp) so temporaries are freed (was a leak); skip unreachable fall-through destroys.
- **Collections RC:** `Array` store/copy cascades element-wise (fixes `Array[Text]` double-free);
  keeping a value read from a container copies it (fixes heap-value `getitem` double-free);
  `Array[Entity]` no longer wrongly assignable.
- **Record layout** uses natural member alignment (not size) so `SizeBytes`/`placeof`/ABI match the
  emitted LLVM (C) layout; codegen resolves Text/Bytes fields by name, not hardcoded index.
- `crash_message` returns `Text` via the sret ABI (a by-value return garbled the crash report).
- SF entity field **reassignment** no longer crashes; SF `Set`/`Dict` literals into an entity slot
  stay bare (roamed at binding); SF-wrapper self-recursion killed via module/realm-distinct resolution.
- Wrong-arity explicit type args on a constructor give a clean **RF-S102** (was a crash).

### Internal / Removed

- `RcRetainLoweringPass` deleted — redundant retain bumps caused double-frees; scope-exit teardown and
  the type's own copy DERIVE handle RC lifecycle.
- Codegen was moved off stdlib-encroachment: monomorphization completes before codegen (no codegen
  type-substitution), and Roamed lock/promote/projection + roam-hook refs moved to dedicated lowering
  passes. The 10 pipeline-stage source folders and compiler phases are renumbered to execution order.

### Performance

- **Warm-compile / daemon groundwork** (milestone-1): warm-state capture/restore proving the
  stdlib-reuse ceiling (SA 5.2s → 31ms), a variant-reuse gate (warm analyze 5.3s → 1.17s), and a
  codegen-correct warm path (emits the identical routine set as a cold build). The remaining dominant
  cost (variant regeneration/reanalysis over the stdlib each compile) is profiled and documented.

---

_Older releases (`v0.0.1-alpha` … `v0.3.0`) predate this changelog._
