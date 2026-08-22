# Suflae v0.1.0 — preview

The first public preview of **Suflae**, RazorForge's sharing-first sister language. Where RazorForge is
ownership-first (someone owns a value; sharing is opt-in), Suflae is **sharing-first**: values are shared
by default and their memory is managed for you. RazorForge's low-level surface — ownership transfer,
manual teardown, iterator/reshaping rules, the danger gate — is deliberately **invisible** in Suflae
unless it earns its place at Suflae's altitude.

This is a **preview**: single-threaded Suflae programs run end-to-end today. Concurrency (a multi-threaded
collector) is the next milestone and is intentionally out of scope here — see **Scope & limits** below.

The compiler is the same binary as RazorForge. Run Suflae with the `suflae` command (or `sf`), or by
handing any `.sf` file to `razorforge`:

```
suflae hello.sf
suflae version      # Suflae v0.1.0
```

## 🌊 The model: `Roamed`

Every Suflae `entity` is a **shared, memory-managed value**. Under the hood it lowers to `Roamed` — a
**biased reference count** (fast path for the common single-owner case) that **auto-promotes when a value
escapes**, with a **cycle collector** to reclaim reference cycles the count alone can't. You never write
`steal`, `hold`, `destroy`, or a wrapper type — you just share values and let go of them.

- **`entity` vs `record` is value-vs-shared:** a `record` is copied, an `entity` is shared (not copied).
  That is the whole distinction — no ownership ceremony on top.
- **Nullability** is part of the type surface.
- **Display transparency:** a `Roamed` value represents, diagnoses, and compares as its inner value —
  the wrapper is invisible in output and operators.

## 🔢 Numbers

Suflae's defaults are chosen for clarity, not machine width.

- **`Integer`** is the default for unsuffixed integer literals (arbitrary width, no silent overflow
  surprises).
- **`Decimal`** is base-10 (not binary `F64`) — the default for fractional literals.
- Both are reused from RazorForge's standard library; Suflae keeps RF's `S64`/`F64` literal defaults only
  inside the shared stdlib bodies.

## 📚 Standard library

Suflae reuses RazorForge's `Core` **wholesale** — `List`, `Dict`, `Set`, `Text`, and the rest are the
real RF Core types, resolved through Suflae's world-line so a bare `List` in `.sf` *is* `RF::Core.List`,
roamed per instance and reached through display/operator transparency. There is no forked collections
library to drift.

- The **cycle collector traces into container element buffers** — `List` elements and `Dict`/`Set`
  key/value buffers are visible to collection, so cycles through collections are reclaimed (not leaked as
  opaque leaves).

## 🧪 Equivalence testing

A dedicated `.sf` fixture harness runs Suflae programs end-to-end and **locks RF/SF equivalence**: the
same program expressed in both languages must produce the same output. This is how the shared-stdlib
reuse stays honest.

## 🎯 Scope & limits

This is an honest **0.1 preview**. What works, and what does not yet:

- ✅ **Single-threaded programs run end-to-end** — hello-world through to real programs.
- ✅ Reused RF number model + `Core` stdlib, `entity`→`Roamed` lowering, nullability, single-thread cycle
  collection, container roam-trace.
- ⛔ **Concurrency is not ready.** The cycle collector and mutators can race, so a multi-threaded Suflae
  program can hit use-after-free. Suflae 0.1 is **single-thread only**; a thread-safe collector is the
  headline of the next milestone.
- ⛔ **Runtime object manipulation / reflection** (`ObjectHacker`) and **hot reload / REPL** are planned
  for later versions, not this one.

Suflae hides memory *management*, not memory *finiteness*: out-of-memory and other fatal resource walls
are loud, uncatchable crashes, distinct from recoverable failures you handle with `try_` / `when`.

## ✅ Tests

The `.sf` fixture harness is green and wired into CI alongside RazorForge's suite (**1,511 unit +
analyzer tests**, **188 stdlib fixtures**), including the RF/SF equivalence locks. CI green on Linux,
macOS, and Windows.
