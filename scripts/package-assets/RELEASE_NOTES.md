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

Checksums for every artifact are attached as `checksums-<platform>.txt`.

## Docs & feedback

Language guide: https://razorforge.lumi-dev.xyz/ ·
Issues and design feedback: https://github.com/dj-lumiere/razorforge-suflae/issues
