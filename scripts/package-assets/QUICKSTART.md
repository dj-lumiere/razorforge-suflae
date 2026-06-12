# RazorForge Quick Start

This package is **self-contained**: the compiler, the standard library, the
native runtime, and the LLVM toolchain (clang/opt/lld) are all inside this
folder. You do not need to install anything else to build and run RazorForge
programs.

## 1. Install (puts `razorforge` + `rf` on your PATH)

**Windows**

```bat
install.cmd
# then open a new terminal
```

If you prefer running the PowerShell script directly, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

**Linux / macOS**

```bash
./install.sh
# then open a new terminal (or: source ~/.profile)
```

> macOS note: linking executables uses the system linker stubs from Apple's
> Command Line Tools. If you have ever built anything on this Mac you already
> have them; otherwise run `xcode-select --install` once.
>
> macOS Gatekeeper: this alpha is not notarized by Apple, so a browser-downloaded
> archive gets quarantined and macOS refuses to load the bundled libraries
> (you'd see "libhostfxr.dylib cannot be opened because the developer cannot be
> verified"). `./install.sh` handles this automatically: it clears the
> quarantine attribute *and* ad-hoc re-signs the bundled binaries (quarantine
> removal alone is not always enough — Apple Silicon requires valid signatures,
> and newer macOS caches Gatekeeper verdicts). If you still hit a dialog, run
> these two commands once from the extracted folder:
>
> ```bash
> xattr -dr com.apple.quarantine .
> find . -type f \( -name '*.dylib' -o -perm -u+x \) -exec codesign --force --sign - {} \;
> ```
>
> Downloading with `curl -LO` avoids the quarantine flag entirely.
>
> Linux note: linking needs the glibc development files (`crt1.o`). Most dev
> machines have them; otherwise `sudo apt install libc6-dev` (Debian/Ubuntu)
> or `sudo dnf install glibc-devel` (Fedora) once.

## 2. Hello, world

Create `hello.rf`:

```razorforge
module Hello

import IO/Console

routine start()
  show("Hello from RazorForge!")
  return
```

Build and run it:

```bash
razorforge buildandrun hello.rf
```

## 3. A real project

Create a `razorforge.toml` next to your sources:

```toml
[package]
name = "my-app"

[target]
executable = "Hello"     # entry module name (the `module` declaration)
mode = "debug"           # debug | release | release-time | release-space
```

Then from anywhere inside the project:

```bash
razorforge buildandrun
```

## Using an AI assistant?

RazorForge is too new to be in any model's training data — assistants will
confidently generate wrong syntax (positional arguments, missing `return`s,
inline conditionals). Give your assistant the **`RAZORFORGE-FOR-AI.md`** file
in this folder first; it lists exactly where their assumptions break, plus the
90+ verified example programs to copy from.

## CLI reference

```
razorforge parse <file>             Parse and show AST summary
razorforge tokenize <file>          Show tokens
razorforge codegen <file> [out.ll]  Emit LLVM IR for one file
razorforge build [entry] [out.ll]   Build a multi-file project
razorforge buildandrun [entry]      Build, link, and execute
razorforge check [entry]            Type-check only
razorforge version                  Show version
```

Docs: https://razorforge.lumi-dev.xyz/ · Issues:
https://github.com/dj-lumiere/razorforge-suflae/issues

## Using your own LLVM instead

The compiler prefers the bundled `toolchain/` folder. To use a system LLVM,
set `RAZORFORGE_LLVM_HOME` to its root (the directory containing `bin/clang`),
or delete the `toolchain/` folder to fall back to PATH lookup.
