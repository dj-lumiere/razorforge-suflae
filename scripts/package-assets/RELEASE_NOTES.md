# RazorForge v0.1.0

The first release with **threaded routines** and a ground-up overhaul of the numeric stack. This is
the largest release since the initial alphas (134 commits since v0.0.4-alpha).

## ✨ Highlights

- **Threaded routines (Phase 1–3).** Concurrency with safety enforced at compile time: a
  readers-writers conflict checker (RF-S630) that tracks controller identity across `.share()`
  aliases, a thread-argument shareability gate (RF-S632), and a re-entrant exclusive-lock guard that
  fails fast instead of deadlocking.
- **Numerics rebuilt.** Dropped the `decNumber`/`TLFloat` dependencies in favor of an in-house
  softfloat engine plus `libbf`. Checked arithmetic is failable by default — overflow, divide-by-zero,
  and domain errors are now typed and must be handled, so numeric failure is explicit at the call
  site rather than silent.
- **IEEE 754 surface.** `total_order`/`total_order_mag`, signaling vs quiet NaN, `nextUp`/`nextDown`,
  decimal `normalize`, and domain-vs-overflow separation in division across the floating-point and
  decimal types.
- **Wider numeric API.** `Decimal` (full IEEE surface + 70-digit text parse), `Integer`
  (`modpow`/`divmod`/bit ops), `Real` (rounding + selection via `libbf`), and `Complex`/`Cxx`
  (`log2`/`reciprocal`/`to_polar`).
- **Faster softfloat (measured, x86-64):** F128 multiply 14.9 → 10.1 ns, D64 true-division
  119.6 → 39 ns (~3×), and `sqrt` ~3.3× faster — speedups that carry into the transcendentals built
  on them. F16 now works correctly on x86-64.

## 🛡️ Correctness & robustness

- Fixed several double-free and memory-leak paths in record-copy lowering, synthesized destructors,
  and owned-temporary teardown.
- Codegen now emits only reachable routines, backed by an over-prune tripwire and a declare/define
  signature-match invariant.
- Deterministic, OS-independent method resolution (fixes Linux/macOS-only resolution differences).
- Bare member access (`x.name`) now reads a field and no longer silently auto-calls a zero-arg method
  — write `x.name()` to call.

## 📦 Packaging

- Tests moved to a dedicated project; the shipped compiler no longer carries test code or
  test/tooling assemblies, slimming the distribution.

## ⚠️ Upgrading from v0.0.4-alpha (source-breaking)

Code written against v0.0.4-alpha may need these adjustments:

1. **Checked numeric ops are now failable (`!`).** Checked fixed-width arithmetic, division, and the
   overflow/domain-prone methods (e.g. `divmod`, `modpow`, `logb`/`quantize`/`scaleb`, `tgamma`) now
   throw typed errors (`NumericOverflow` / `DivisionByZero` / `NumericDomain`). Callers must be
   failable themselves, handle with `when`, or call the generated `try_`/`check_` variants.
   Arbitrary-precision `Integer` add/sub/mul stay non-failable (they cannot overflow); their division
   family does not.
   ```
   # before
   var q = a.divmod(other: b)
   # after — handle the failure
   var q = a.divmod!(other: b)        # in a failable routine
   var q = a.try_divmod(other: b)     # or recover explicitly
   ```

2. **Bare member access no longer auto-calls a zero-arg method (RF-S450).** `x.name` reads a member
   *variable*; call methods explicitly with `()`.
   ```
   var p = cstr.ptr      # before (silently called ptr())
   var p = cstr.ptr()    # after
   ```

3. **Error type rename:** `ValueError` / `ParseError` → `InvalidValueError`. Update `when` arms and
   any explicit references.

4. **`Decimal.$sub` is now `$sub!`** (subtraction can overflow). Decimal subtraction in non-failable
   contexts must be handled.

5. **New concurrency static checks may reject previously-accepted code:** RXW conflicts (RF-S630),
   unshareable thread arguments (RF-S632). These reject data-racing patterns that compiled before but
   were unsafe; re-entrant exclusive-lock acquisition now crashes (`ReentrantLockError`) rather than
   deadlocking. The fix is usually to share through `Atomic`/`Shared`/`Watched` or restructure the
   `using` scopes.

## ✅ Platforms

Verified in CI on Linux x86-64, Windows x86-64, and macOS arm64 (Apple Silicon).
