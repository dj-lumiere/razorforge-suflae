# RazorForge v0.3.0

The channels release. v0.2.0 shipped the execution model — coroutines, threads, and a single owned
`Agent[T]` handle over both. v0.3.0 adds the communication layer that was promised next: **typed
channels** for streaming values between concurrent work, a **`SignalCaster`** condition-variable monitor
for hand-built coordination, and an **M:N work-stealing scheduler** so coroutines actually run in
parallel across cores. Failure stays marked (`!`) and ownership transfer stays marked (`steal`) all the
way through the new surface.

## 📡 Channels

Typed streaming conduits between producers and consumers — the piece v0.2.0 explicitly deferred.

- **`Sender[T]`** — the producer end; **cloneable** (`sender.clone()`), so many producers can feed one
  channel (fan-in via `steal sender.clone()`). Send with `send!(item:)`; `close()` / `is_closed()`
  manage the lifecycle.
- **`Receiver[T]`** — a single-consumer receiver, directly **iterable** (`obeys Iterable[T]`).
- **`SharedReceiver[T]`** — a multi-consumer (MPMC) receiver for worker-pool patterns, also iterable
  and cloneable.
- **`ChannelDrain[T]`** — the emittable iterator (`$emit!`) that iteration drains a receiver through.
- **Factories:** `make_channel[T](capacity:) -> (Sender, Receiver)` and
  `make_shared_channel[T](capacity:) -> (Sender, SharedReceiver)`.
- **`send!` is failable** — a closed channel is a marked failure you handle with `when` / `try_`, never
  a silent drop. Bounded capacity gives natural backpressure: a full channel parks the producing
  coroutine (or blocks the producing thread) until a consumer makes room.

## 📶 SignalCaster

A condition-variable monitor with its own internal mutex, for coordination patterns channels don't cover.

- **`lock` / `unlock` / `wait` / `wait_within(deadline)` / `cast_one` / `cast_all` / `clone`.**
- **`wait` is uncolored** — a coroutine parks, a thread blocks, same call site (the same contract as
  `retrieve!` and `waitfor`). `wait_within` adds a timed variant.
- Predicate-style waits and timeouts are covered end-to-end.

## ⚙️ M:N work-stealing scheduler

The v0.2.0 scheduler was a per-thread, caller-driven event loop — a coroutine only advanced while its
owning thread was inside an await. v0.3.0 replaces it with a **process-global pool of N daemon worker
threads** (N = host cores by default) with **per-worker work-stealing**, so independent coroutines make
progress on multiple cores at once.

- **Per-worker local deques** (owner pushes/pops one end, idle workers steal from the other) plus a
  shared injector queue for off-pool spawns and wakes.
- **`RF_WORKERS`** environment knob pins the worker count (`RF_WORKERS=1` for deterministic,
  single-worker execution); otherwise it tracks host cores.
- A **worker-safe park/wake state machine** keeps a wake that races a park from ever resuming a
  coroutine on two workers at once, and the deadlock detector is N-aware — it only flags a genuine
  stall once *every* worker is idle with work outstanding.
- Coroutines may **migrate** between workers across a park; single-thread-only access tokens
  (`Viewing`/`Modifying`) and bare entities are held to their thread, while the thread-shareable set
  (`Atomic`/`Shared`/`Watched`/`Inspecting`/`Claiming`) may cross.

## 🛡️ Thread-crossing soundness

Passing a value into a `threaded` or `suspended` routine now **checks at compile time** that it is safe
to share across the boundary (RF-S632). A bare, single-owner entity may cross only when moved with
`steal` (the move is provably exclusive); shared-ownership handles must cross as `Shared`/`Watched`.
Single-thread tokens are rejected at the boundary rather than racing at runtime.

## 🩹 Runtime stability

- **Per-thread coroutine context on the M:N pool.** The stackful-coroutine backend's active-context
  pointer is now correctly thread-local under the multi-worker scheduler, fixing an intermittent
  crash that surfaced only once coroutines ran on more than one worker thread.

## ✅ Tests

Full stdlib end-to-end suite green — **163 fixtures**, including the new `channel_*` (backpressure,
fan-in, rendezvous, try-feed, worker-pool, introspection), `signalcaster_*` (predicate, timeout), and
`coro_*` scheduler fixtures (migration, parallel, work-steal) — alongside the analyzer and unit suites
(1,642 tests total). CI green on Linux, macOS, and Windows.

## ⚠️ Not yet

**Async networking** is still not implemented (async file I/O and subprocess orchestration from v0.2.0
remain the async I/O surface). The **Suflae** sister language is in progress and not part of this
release.
