## What's new in v0.0.3-alpha

A bugfix release that closes three correctness gaps: a parser misread of
subjectless `when`, a memory leak on entity-variable reassignment, and protocol
conformance that ignored category membership.

### Compiler fixes

- **Subjectless `when` arms no longer misparse as lambdas.** A `when` with no
  subject whose arm condition started with an identifier (e.g. `x > 0`) could
  be mistaken for a lambda parameter list, producing a spurious syntax error.
  Arm conditions now parse correctly.
- **Reassigning a plain entity variable no longer leaks.** Assigning a new
  value to an entity-typed `var` now destroys the previous contents first,
  matching the teardown that already ran at end of scope. Branch- and
  loop-driven reassignment no longer leaks the old value.

### Language

- **Records satisfy category protocols by membership.** A record now conforms
  to category protocols such as `RecordType` purely by being a record — no
  explicit `obeys` clause required. Conformance is decided by category
  membership rather than only by declared protocol lists.

All three fixes are locked by end-to-end fixtures that run green on every
commit.

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
