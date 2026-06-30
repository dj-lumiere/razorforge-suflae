# RazorForge v0.2.0

The concurrency release. RazorForge gains a full asynchronous execution model — stackful coroutines, a
cooperative scheduler, OS threads, and a single owned handle that unifies both — plus structured
concurrency, subprocess orchestration, and async file I/O. The design goal throughout: concurrency you
write as straight-line code, with ownership and failure stayed explicit.

## 🧵 Concurrency model

- **`suspended routine` and `threaded routine`.** Calling one *starts* the work and hands back an
  owned **`Agent[T]`** — no `spawn` keyword, the call is the spawn. A `suspended` routine runs as a
  stackful coroutine on this thread's implicit scheduler; a `threaded` routine runs on an OS thread.
  One handle type backs both, so a mixed set can be awaited together.
- **`agent.retrieve!()` — uncolored await.** Drives the work and returns its value. Inside a
  scheduler-driven coroutine it *parks* (siblings keep running); on a plain thread it blocks. No
  function coloring: the same call site works in either context.
- **`waitfor(duration)`** — uncolored timed wait (parks under the scheduler, sleeps on a thread), and
  **`agent.waitfor(d).retrieve!()`** for a per-agent timeout.
- **Drop = abandon.** An un-retrieved `Agent` that goes out of scope is cleanly torn down: a parked
  coroutine unwinds its cancellation shadow stack; a running worker thread is joined then discarded.

## 🪢 Structured concurrency

A `List[Agent[T]]` *is* the scope — its ownership already guarantees no child outlives it. The scope
operations are member routines on that list:

- **`agents.gather!()`** — drive all concurrently, return every result in input order, fail-fast.
- **`agents.race!()`** — drive all, return the first finisher's value; losers are abandoned.
- **`agents.cancel_all!()`** — request cooperative cancellation of every agent, then wait out the
  wind-down. Cancellation is request-only and never frees (teardown stays at scope exit); an agent
  observes it via `cancellation_requested()` (the only way to halt a worker thread, which cannot be
  killed) or via an interruptible `waitfor`.

## ⚙️ Runtime

- **Stackful coroutines** backed by native fibers on Windows (`CreateFiberEx`) and libco elsewhere, so
  a coroutine can suspend from any call depth — including deep C-runtime calls like `fopen`.
- **Cooperative scheduler** (ready FIFO + timer list) with **cross-thread wake**, the bridge that lets
  a coroutine await a worker thread without blocking and lets parked work resume from any thread.
- **Demand-committed coroutine stacks** — reserve large, commit on touch — so very many coroutines
  coexist (≈14 KB resident per live coroutine, not 1 MiB); allocation failure raises a diagnosed
  `OutOfMemoryError` instead of crashing.

## 🔌 Subprocess & async I/O

- **`run_process(command) -> ProcessResult`** — run an external program (shell), capturing stdout,
  stderr, exit code, and signal, while parking the calling coroutine. Orchestrate programs
  concurrently with `gather!`.
- **Uncolored file I/O** — `read_text(path)` and `write_text(path, content)` carry no `_async`
  variant and no function color. Inside a `suspended routine` the calling coroutine *parks* while a
  vendored libuv loop on its own thread does the blocking transfer (siblings keep progressing);
  outside one it runs inline. Same call site, either context — the same contract as `retrieve!` and
  `waitfor`. For whole-file work prefer these to opening a `FileHandle` and calling the blocking
  `read_all` inside a coroutine.

## 🛠️ Language & compiler

- **Member routines on specialized generic receivers** — e.g. `routine List[Agent[V]].gather!()`,
  where `me` is typed as the specialized receiver. This is what lets the structured-concurrency
  operations live directly on `List[Agent[T]]`.
- **Named-argument evaluation order fixed** — named arguments are bound to parameters by name (not
  source order) across every call path; order-independent named calls now evaluate and bind correctly.

## ⚠️ Not yet

Honesty about scope: **channels** (typed streaming conduits) are designed but land in **v0.3.0**, and
**async networking** is not yet implemented. v0.2.0 is the execution model; streaming/communication
come next.

## ✅ Tests

Full stdlib end-to-end suite green — 146 fixtures across coroutines, threads, `Agent`/`race!`/
`gather!`/`cancel_all!`, subprocess, and async I/O — alongside the analyzer and unit suites (1615
tests total). CI green on Linux, macOS, and Windows.
