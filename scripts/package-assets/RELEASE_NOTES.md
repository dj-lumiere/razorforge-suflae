## What's new in v0.0.2-alpha

This release is about **decimal precision you can trust** — every decimal type
now has a complete, correctly-rounded transcendental surface — plus a rework of
deferred initialization.

### Numerics

- **`F128` is now backed by TLFloat** (correctly rounded quad-precision
  software float). LLVM's `fp128` type is gone from emitted IR entirely, which
  removes the platform-specific compiler-rt shims (notably on macOS).
  Behavioral fixes that come with it: subnormals are computed correctly (they
  were flushed before), `F128`→`F64` conversion rounds (it truncated), and
  large-integer→`F128` conversion rounds to nearest.
- **`D32`/`D64`/`D128` gain the full transcendental surface** — `sin` `cos`
  `tan` `asin` `acos` `atan` `atan2`, the hyperbolics and their inverses,
  `exp` `exp2` `expm1` `log` `log2` `log10` `log1p`, `pow`/`$pow!`, `cbrt`,
  `hypot`. Results are **correctly rounded to the type's last digit and
  byte-identical on every platform**, via tiered routing through the
  next-size-up TLFloat binary format (D32 → binary64, D64 → quad, D128 →
  octuple).
- **Arbitrary-precision `Decimal` trig now actually exists.** `sin` … `tanh`,
  `atan2`, `Decimal.pi(precision)`, and `Decimal.e(precision)` were declared
  in the stdlib but had no implementation — any call failed at link. They are
  now LibBF-backed with working precision that scales with the request
  (default 50 significant digits, up to 1000), so a 1000-digit result is as
  trustworthy as a 50-digit one.
- **Canonical special values everywhere:** every float and decimal type now
  prints `inf`, `-inf`, and `NaN` (NaN is always unsigned). Previously the
  spellings differed by family (`-nan`, `Inf`, `Infinity`).

### Language

- **Breaking:** the `uninit` keyword is removed. Use `lateinit var` instead.
- **`lateinit` reworked — eager allocation, late initialization.** Storage is
  allocated at the declaration, so a `lateinit` entity is valid and borrowable
  immediately (the out-parameter pattern now works instead of crashing), and
  reassignment destroys the previous contents, so branch-initialization no
  longer leaks.

- **Typed suffixes now work on integer-form literals.** `1f64`, `1d32`, `1dn`,
  `1j`, `1jn`, … all tokenize — a literal no longer needs a decimal point just
  to carry a float/decimal/imaginary suffix.
- **Complex values print as `a+bj`**, matching the imaginary literal suffixes,
  so output round-trips as literal syntax. The fixed-width `C32`/`C64`/`C128`
  gain this representation too (they previously printed the memberwise debug
  form), and the arbitrary-precision `Complex` switches from `1+1i` to `1+1j`.

### Compiler fixes

- **Default arguments on method calls were silently broken.** Calling any
  method without an argument that has a declared default (e.g. `d.sin()`,
  `Decimal.pi()`) emitted a call with the argument *missing* — the callee read
  whatever happened to be in the register, producing nondeterministic results
  that varied by platform. Defaults are now materialized at every call site.

All numeric results above are locked by end-to-end fixtures whose expected
outputs were generated from the runtime itself and cross-checked against
reference constants.

---

**RazorForge** is a natively compiled, statically typed language built around
single-ownership memory management (no borrow checker, no GC) and
compiler-generated error handling. This is an early alpha: the compiler,
runtime, and standard library work — 1,400+ tests and 90+ end-to-end fixtures
run green on every commit — but APIs will change and you will find bugs.

## Install & hello world

Each package is **self-contained**: compiler, standard library, native runtime,
and the LLVM 22 toolchain (clang/opt/lld) are all inside — no separate LLVM
install needed.

1. Download the package for your platform below and extract it.
2. Run the installer to put `razorforge` (and `rf`) on your PATH:
    - **Windows**: `install.cmd`
    - **Linux / macOS**: `./install.sh`
3. Write `hello.rf`:

   ```razorforge
   module Hello

   import IO/Console

   routine start()
     show("Hello from RazorForge!")
     return
   ```

4. `razorforge buildandrun hello.rf`

See `QUICKSTART.md` inside the package for projects, `razorforge.toml`, and the
full CLI reference.

### Platform notes

| Package     | Needs from the system                                                                     |
|-------------|-------------------------------------------------------------------------------------------|
| `win-x64`   | Nothing — fully self-contained (mingw-based linking)                                      |
| `linux-x64` | glibc development files for linking (`apt install libc6-dev` / `dnf install glibc-devel`) |
| `osx-arm64` | Apple Command Line Tools for linker stubs (`xcode-select --install`)                      |

> **macOS Gatekeeper:** this alpha is not notarized by Apple, so browser
> downloads are quarantined and macOS will refuse to load the bundled
> libraries ("libhostfxr.dylib cannot be opened because the developer cannot
> be verified"). `./install.sh` fixes this automatically — it clears the
> quarantine attribute and ad-hoc re-signs the bundled binaries (quarantine
> removal alone isn't always enough: Apple Silicon requires valid signatures
> and newer macOS caches Gatekeeper verdicts). Manual equivalent, run once
> inside the extracted folder:
> `xattr -dr com.apple.quarantine . && find . -type f \( -name '*.dylib' -o -perm -u+x \) -exec codesign --force --sign - {} \;`
> Downloading with `curl -LO` avoids the quarantine flag entirely.

Checksums for every artifact are attached as `checksums-<platform>.txt`.

## Docs & feedback

Language guide: https://razorforge.lumi-dev.xyz/ ·
Issues and design feedback: https://github.com/dj-lumiere/razorforge-suflae/issues
