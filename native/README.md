# RazorForge Native Runtime

This directory contains the native C runtime and vendored libraries used by RazorForge and, later, Suflae.

It is not just a math library folder. It is the runtime substrate for:

- memory allocation and crash reporting
- text/console/file primitives
- fixed-width numeric support that does not map cleanly to plain LLVM intrinsics
- future stdlib wrappers over native libraries
- task runtime, `threaded routine`, `suspended routine`, `waitfor`, `within`, and `after`
- future actor and GC runtime work for Suflae

The current design direction is reflected in:

- [internal-wiki/FUTURE-STDLIB-TODO.md](../internal-wiki/FUTURE-STDLIB-TODO.md)
- [internal-wiki/FUTURE-STDLIB-API.md](../internal-wiki/FUTURE-STDLIB-API.md)

## Purpose

The native layer exists for three different reasons.

1. Platform/runtime services

- allocation and invalidation
- crash reporting and trace collection
- console I/O and file I/O
- time, clocks, and waiting
- OS threads, green-thread contexts, async I/O polling

2. External library wrapping

- decimal, big integer, crypto, regex, compression, JSON/TOML/CSV, database, GC
- only the parts the stdlib actually exposes should cross the FFI boundary

3. Runtime-only infrastructure

- task state objects
- scheduler backends
- lock/refcount controllers
- actor/GC runtime internals later

The rule is:

- stdlib owns the language-facing API shape
- compiler owns lowering
- `native/` owns platform work and runtime state

## Current Layout

```text
native/
├── CMakeLists.txt
├── build.bat
├── build.sh
├── include/
│   └── razorforge_runtime.h
├── runtime/
│   ├── runtime_init.c
│   ├── cstring_runtime.c
│   ├── console_runtime.c
│   ├── duration_wait.c
│   ├── crash_runtime.c
│   ├── shared_locks.c
│   ├── memory.c
│   ├── stacktrace.c
│   ├── text_functions.c
│   ├── file_functions.c
│   ├── moment.c
│   ├── task_runtime.c
│   ├── concurrency_context.c
│   ├── async_io.c
│   ├── math_functions.c
│   ├── decimal_functions.c
│   ├── bignum_functions.c
│   ├── f16_functions.c
│   ├── f128_functions.c
│   ├── csharp_interop.c
│   └── types.h
├── cmake/
│   └── *.cmake
└── <vendored libraries>/
```

## Runtime Layers

The native runtime is moving toward four layers.

### 1. Core Runtime

Files:

- [runtime_init.c](../native/runtime/runtime_init.c)
- [cstring_runtime.c](../native/runtime/cstring_runtime.c)
- [console_runtime.c](../native/runtime/console_runtime.c)
- [duration_wait.c](../native/runtime/duration_wait.c)
- [crash_runtime.c](../native/runtime/crash_runtime.c)
- [shared_locks.c](../native/runtime/shared_locks.c)
- [memory.c](../native/runtime/memory.c)
- [stacktrace.c](../native/runtime/stacktrace.c)
- [text_functions.c](../native/runtime/text_functions.c)
- [file_functions.c](../native/runtime/file_functions.c)
- [moment.c](../native/runtime/moment.c)

Responsibilities:

- `rf_runtime_init`
- `rf_cstr_*`
- `rf_console_*`
- `razorforge_mutex_*` / `razorforge_rwlock_*`
- `rf_allocate_dynamic`, `rf_reallocate_dynamic`, `rf_invalidate`
- `rf_crash`, `rf_trace_push`, `rf_trace_pop`
- console and file primitives
- clock/time primitives
- `rf_waitfor_duration` for `waitfor 5s`

This layer is scheduler-agnostic. It should not know whether code is running in a normal routine, a threaded routine, or
a suspended routine.

### 2. Task Runtime

Files:

- [task_runtime.c](../native/runtime/task_runtime.c)
- [Task.rf](../Standard/RazorForge/Core/Types/Task.rf)

Responsibilities:

- runtime-owned `rf_task`
- task kind: suspended vs threaded
- task status and completion state
- result/error payload storage
- wait/timeout hooks
- prerequisite/dependent bookkeeping for `after`

This is the native backing store for language-level `Task[T]`.

The language-facing type is intentionally thin:

- `Task[T]` is an opaque pointer-shaped record
- the real state lives in `rf_task`

### 3. Execution Backends

Files:

- [concurrency_context.c](../native/runtime/concurrency_context.c)
- [async_io.c](../native/runtime/async_io.c)

Responsibilities:

- stackful context switching backend for `suspended routine`
- async event loop backend for timers, wakeups, and I/O readiness

Current backend choices:

- `libco` for stackful context switching
- `libuv` for async I/O and event loop services

The public rule is:

- RazorForge code should call `rf_context_*` and `rf_async_*`
- nothing outside `native/runtime` should call `libco` or `libuv` APIs directly

Right now both wrappers are still stubs. The wrapper boundary is deliberate: it keeps the third-party library choice
replaceable later.

### 4. Numeric and External-Library Bridges

Files:

- [decimal_functions.c](../native/runtime/decimal_functions.c)
- [bignum_functions.c](../native/runtime/bignum_functions.c)
- [f16_functions.c](../native/runtime/f16_functions.c)
- [f128_functions.c](../native/runtime/f128_functions.c)
- [math_functions.c](../native/runtime/math_functions.c)

Responsibilities:

- expose C-callable bridges for stdlib numeric types
- isolate third-party numeric/runtime dependencies behind `rf_*` entrypoints

## Stdlib Relationship

The native layer should follow the stdlib structure, not invent a second public API.

Examples:

- `Core/NativeDeclarations.rf` declares the raw FFI surface
- `Core/Types/Task.rf` wraps task handles
- `Core/Memory/Wrapper/Shared.rf`, `Marked.rf`, `Retained.rf`, `Tracked.rf` describe the ownership model the runtime
  must eventually support

Important split from the stdlib roadmap:

- `Maybe[T]` is a real data carrier
- `Result[T]` and `Lookup[T]` are ephemeral control-flow carriers
- `Task[T]` is a runtime handle, not user-owned inline state

That means the native runtime must prioritize:

- task state and completion handling
- waiting, timeout, and scheduler hooks
- ownership/reference operations for RC/ARC wrappers

not just generic “async utilities”.

## Concurrency Direction

The current concurrency model is:

- `threaded routine` -> OS-thread-backed task
- `suspended routine` -> green-thread-backed task
- `waitfor task` -> wait for `Task[T]`
- `waitfor duration` -> timed suspension/blocking
- `within` -> timeout
- `after` -> dependency graph between tasks

Planned runtime split:

- `task_runtime.c`
  task state, completion, dependencies
- `concurrency_context.c`
  stackful green-task context backend
- `async_io.c`
  timer + async event backend
- future OS thread backend
  native thread spawn/join/wakeup

The high-level rule is:

- `threaded routine` and `suspended routine` share one task model
- they differ only in execution backend and waiting behavior

## Ownership / Memory Model Staging

The stdlib already establishes these wrapper families:

- `Retained[T]`: RC holding
- `Tracked[T]`: RC observing
- `Shared[T, P]`: ARC holding
- `Marked[T, P]`: ARC observing
- `Viewed[T]`, `Hijacked[T]`, `Inspected[T]`, `Seized[T]`: temporary access wrappers
- `Snatched[T]`: raw unmanaged pointer escape hatch

The native runtime should eventually mirror that split directly:

- RC runtime
- ARC runtime
- lock-policy runtime
- task/scheduler runtime

It should not collapse all of that into one anonymous “heap handle” abstraction.

## Vendored Libraries

These directories are vendored snapshots, not separate repos the runtime should expose directly.

### Core numeric/runtime support

- `libbf`
- `decNumber`
- `libtommath`

### Data and text adjacent libraries

- `utf8proc`
- `yyjson`
- `tomlc99`
- `minicsv`
- `pcre2`

### Compression and storage

- `zlib`
- `zstd`
- `sqlite3`

### Crypto and TLS

- `libsodium`
- `mbedtls`

### Concurrency / async backends

- `libco`
- `libuv`

### GC for future Suflae runtime

- `bdwgc`

Not every vendored library is “fully integrated”. Some are linked today, some are only staged for future stdlib modules.
The runtime/stdlib should expose a stable `rf_*` API either way.

## Build System

Primary files:

- [CMakeLists.txt](../native/CMakeLists.txt)
- [build.bat](../native/build.bat)
- [build.sh](../native/build.sh)
- [cmake/libco.cmake](../native/cmake/libco.cmake)
- [cmake/libuv.cmake](../native/cmake/libuv.cmake)

### Build outputs

- `razorforge_runtime`
  shared runtime library used by compiled programs
- `razorforge_math`
  static math-focused support library

### Build commands

Windows:

```powershell
native\build.bat
```

Linux/macOS:

```bash
./native/build.sh
```

Direct CMake:

```powershell
cmake -S native -B native/build
cmake --build native/build --target razorforge_runtime --config Release
```

## Current Reality vs Planned Reality

Current reality:

- `rf_task` exists
- `Task[T]` exists
- `waitfor task` lowers through runtime calls
- `waitfor 5s` lowers to `rf_waitfor_duration`
- `libco` and `libuv` are vendored and wrapped
- `concurrency_context.c` and `async_io.c` are still backend stubs
- task waiting is still shallow and not yet a real blocking/parking scheduler

Planned reality:

- `threaded routine` spawns real OS-thread-backed tasks
- `suspended routine` runs on a real scheduler backed by `libco`
- `waitfor` parks suspended tasks instead of blocking worker threads
- `within` uses timer-backed wakeups
- `after` uses real dependent task activation
- ARC/RC wrappers get native lifecycle support
- Suflae actor and GC layers build on top of the same substrate

## Design Rules

When adding new native functionality, follow these rules.

1. Do not expose third-party APIs directly to the compiler or stdlib.

- good: `rf_async_runtime_run_once(...)`
- bad: `uv_run(...)` in generated code or stdlib declarations

2. Keep the `rf_*` boundary narrow and purposeful.

- raw runtime hooks belong here
- high-level policy belongs in stdlib/compiler

3. Match stdlib semantics exactly.

- if the stdlib says `Task[T]` is opaque, the runtime should not require users to reason about the underlying struct
- if `waitfor duration` is language-level sleep, the native name should reflect that role

4. Prefer one runtime-owned state object over scattering task state across ad hoc globals.

5. Treat vendored libraries as replaceable backends.

- especially `libco` and `libuv`

## Immediate Priorities

The next native work should focus on concurrency and ownership, not more one-off FFI sprawl.

### Concurrency

- real threaded task spawn and join
- real suspended-task scheduling
- timer-backed `within`
- parked wait queues for `waitfor`
- dependency activation for `after`

### Ownership runtime

- runtime support for `Retained`, `Tracked`, `Shared`, `Marked`
- proper drop/clone hooks for record-stored wrappers
- lock/runtime integration for `Inspected` and `Seized`

### Stdlib-backed wrappers

- FileSystem
- Networking
- Compression
- RegularExpression via PCRE2-32
- Json/Toml/Csv bridges where wrapping is valid

## Notes

- `build/` is generated output and should not be treated as source.
- Vendored dependency directories under `native/` are intentionally plain folders, not nested repos.
- If a feature is language-visible, prefer documenting the user-facing behavior in the wiki or stdlib docs, and document
  the backend/runtime obligations here.
