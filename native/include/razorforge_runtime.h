#ifndef RAZORFORGE_RUNTIME_H
#define RAZORFORGE_RUNTIME_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct rf_context_runtime rf_context_runtime;
typedef struct rf_async_runtime rf_async_runtime;
typedef struct rf_task rf_task;
typedef struct rf_coro rf_coro;
typedef struct rf_sched rf_sched;

typedef void (*rf_context_entry_fn)(void* userdata);
typedef void (*rf_task_entry_fn)(rf_task* task, void* userdata);

typedef enum rf_task_kind {
    RF_TASK_SUSPENDED = 0,
    RF_TASK_THREADED = 1
} rf_task_kind;

typedef enum rf_task_status {
    RF_TASK_NEW = 0,
    RF_TASK_READY = 1,
    RF_TASK_RUNNING = 2,
    RF_TASK_PARKED = 3,
    RF_TASK_COMPLETED = 4
} rf_task_status;

typedef enum rf_task_completion_kind {
    RF_TASK_COMPLETION_PENDING = 0,
    RF_TASK_COMPLETION_VALUE = 1,
    RF_TASK_COMPLETION_ERROR = 2,
    RF_TASK_COMPLETION_CANCELLED = 3,
    RF_TASK_COMPLETION_TIMEOUT = 4
} rf_task_completion_kind;

typedef struct rf_task_completion {
    rf_task_completion_kind kind;
    void* value_payload;
    void* error_payload;
} rf_task_completion;

typedef enum rf_runtime_backend_state {
    RF_RUNTIME_BACKEND_UNAVAILABLE = 0,
    RF_RUNTIME_BACKEND_AVAILABLE = 1
} rf_runtime_backend_state;

const char* rf_context_backend_name(void);
rf_runtime_backend_state rf_context_backend_state(void);

const char* rf_async_backend_name(void);
rf_runtime_backend_state rf_async_backend_state(void);

rf_context_runtime* rf_context_runtime_create(void);
void rf_context_runtime_destroy(rf_context_runtime* runtime);
int rf_context_runtime_spawn(rf_context_runtime* runtime, rf_context_entry_fn entry, void* userdata, size_t stack_size);

rf_async_runtime* rf_async_runtime_create(void);
void rf_async_runtime_destroy(rf_async_runtime* runtime);
int rf_async_runtime_run_once(rf_async_runtime* runtime);
int rf_async_runtime_run_default(rf_async_runtime* runtime);
void rf_async_runtime_stop(rf_async_runtime* runtime);

/* Coroutine I/O parking: read a whole file off the libuv threadpool while the calling coroutine is
 * parked (siblings keep running). Returns a malloc'd NUL-terminated buffer (caller frees), with the
 * byte count readable via rf_io_get_result_len; NULL + length 0 on error. Falls back to a synchronous
 * read outside a scheduler-driven coroutine. */
char* rf_io_read_file_all(const char* path, int32_t path_len);
uintptr_t rf_io_get_result_len(void);
/* Write data_len bytes to a file (truncating) while the calling coroutine is parked; the payload is
 * copied so the caller's buffer need not outlive the call. Returns bytes written, or -1 on error.
 * Falls back to a synchronous write outside a scheduler-driven coroutine. */
int64_t rf_io_write_file_all(const char* path, int32_t path_len, const char* data, int64_t data_len);
/* Eagerly start the async I/O loop on the main thread at process startup (called by rf_runtime_init). */
void rf_io_runtime_init(void);

/* Run a shell command, capturing stdout/stderr, while the calling coroutine is parked (it blocks on a
 * plain thread). Returns the child's exit code, or -1 if it could not be spawned. The captured streams
 * and terminating signal are read afterwards via the accessors below; the output buffers are malloc'd,
 * NUL-terminated, and owned by the caller. */
int64_t rf_proc_run(const char* command, int32_t command_len);
int32_t rf_proc_term_signal(void);   /* signal that killed the child, or 0 if it exited normally */
char* rf_proc_output(void);          /* captured stdout (malloc'd, NUL-terminated) */
uintptr_t rf_proc_output_len(void);
char* rf_proc_errors(void);          /* captured stderr (malloc'd, NUL-terminated) */
uintptr_t rf_proc_errors_len(void);

/* Argv builder: spawn an executable directly with an explicit argument vector (no shell). begin sets
 * argv[0] = file; add_arg appends; set_cwd is optional (empty = inherit); run_built launches it
 * (same parking/blocking + accessors as rf_proc_run) and consumes the builder. */
typedef struct rf_proc_builder rf_proc_builder;
rf_proc_builder* rf_proc_begin(const char* file, int32_t file_len);
void rf_proc_add_arg(rf_proc_builder* builder, const char* arg, int32_t arg_len);
void rf_proc_set_cwd(rf_proc_builder* builder, const char* cwd, int32_t cwd_len);
/* Add an environment override (merged into the parent environment, not replacing it). */
void rf_proc_add_env(rf_proc_builder* builder, const char* key, int32_t key_len, const char* val, int32_t val_len);
int64_t rf_proc_run_built(rf_proc_builder* builder);

const char* rf_task_kind_name(rf_task_kind kind);
const char* rf_task_status_name(rf_task_status status);
const char* rf_task_completion_name(rf_task_completion_kind kind);

rf_task* rf_task_create(rf_task_kind kind);
void rf_task_destroy(rf_task* task);
void rf_task_release(rf_task* task);
/* Mark a result task as fire-and-forget (`execute()`): rf_task_complete_value frees it + its result
 * box, no consumer. Set before the coroutine/thread runs (no complete-before-detach race). */
void rf_task_set_detached(rf_task* task);
/* Whether a task is fire-and-forget (`execute()`). Read by the generated entry thunk to destroy the
 * result instead of boxing it when detached (so an owned return does not leak). */
uint8_t rf_task_is_detached(rf_task* task);

uint64_t rf_task_id(rf_task* task);

/* Opaque, stable identifier for the calling OS thread. Used by the lock policies to detect a
 * re-entrant claim (a thread acquiring an exclusive lock it already holds = self-deadlock). */
uint64_t rf_current_thread_id(void);

/* Identity of the current logical execution context: the coroutine (rf_coro*) if inside one — stable
 * across worker migration, which is exactly why Roamed's reentrant lock keys on it and NOT the OS
 * thread — else the OS thread id. Only equality matters. See rf_current_task_id in coro_runtime.c. */
uint64_t rf_current_task_id(void);

// Cycle collector (Bacon-Rajan synchronous recycler) — native buffers backing the RF-side collector
// (Core/Memory/CycleCollector.rf). See internal-wiki/v0.4.x-cycle-collector.md.
//
// rf_cyclic_add_candidate is the SOLE collector entry point: a Roamed strong-decrement that leaves the
// count > 0 reports the controller here as a possible cycle root (the RF side dedups via the
// controller `buffered` flag first). Pure-RF programs with no Roamed cycles never call it.
void rf_cyclic_add_candidate(void* obj);
uint64_t rf_cyclic_roots_count(void);
void* rf_cyclic_roots_at(uint64_t i);
void rf_cyclic_roots_clear(void);
void rf_cyclic_roots_remove_front(uint64_t n);  // drop the first n (processed) candidates, keep late ones
void rf_cyclic_roots_remove(void* ptr);         // drop one candidate about to be freed (eager release path)
// scratch = one controller's children, filled by its trace hook and drained by RF.
void rf_cyclic_scratch_reset(void);
uint64_t rf_cyclic_scratch_count(void);
void* rf_cyclic_scratch_at(uint64_t i);
void rf_cyclic_visit_child(void* child_ctrl);      // called by a per-type trace hook
// reap = deferred-free buffer for collected white controllers.
void rf_cyclic_reap_push(void* ctrl);
uint64_t rf_cyclic_reap_count(void);
void* rf_cyclic_reap_at(uint64_t i);
void rf_cyclic_reap_clear(void);
void rf_cyclic_trace_into_scratch(void* trace_hook, void* controller);  // SOLE trace indirect-call site
void rf_cyclic_invoke_free(void* free_hook, void* controller);          // free indirect-call site
// Auto-collection trigger (candidate-set threshold; RF_CC_THRESHOLD env, default 128).
int rf_cyclic_should_collect(void);
void rf_cyclic_enter_collect(void);
void rf_cyclic_exit_collect(void);
// Stop-the-world cooperation: mutators (RoamController hold/unhold) bracket their count/state mutation in
// the shared lock; enter/exit_collect above take it EXCLUSIVE. See coro_runtime.c for the full protocol.
void rf_cyclic_lock_shared(void);
void rf_cyclic_unlock_shared(void);

rf_task_kind rf_task_kind_get(rf_task* task);
rf_task_status rf_task_status_get(rf_task* task);
rf_task_completion_kind rf_task_completion_kind_get(rf_task* task);
void* rf_task_result_payload(rf_task* task);
void* rf_task_error_payload(rf_task* task);
rf_task_completion_kind rf_task_wait(rf_task* task);
rf_task_completion_kind rf_task_wait_within(rf_task* task, int64_t timeout_seconds, uint32_t timeout_nanoseconds);
/* Register the current scheduler-driven coroutine as this task's awaiter. Returns 1 if already
 * complete (read the result, don't park), 0 if registered (park via rf_sched_park_external; the
 * worker wakes you on completion). Outside a scheduler-driven coroutine, returns 1 (block-wait). */
uint32_t rf_task_await_coro(rf_task* task);
/* Timed variant: park (deadline timer + externally wakeable) until the task completes or
 * `timeout_ns` elapses. Returns 1 = completed (read result), 0 = timed out, 2 = not on a
 * scheduler-driven coroutine (block-wait with the deadline instead). */
uint32_t rf_task_await_coro_deadline(rf_task* task, uint64_t timeout_ns);
/* Register (s != NULL) / clear (s == NULL) the scheduler a `race!` over a set including this task is
 * driving, plus the racing coroutine `coro` (NULL for a top-level thread racer). On completion the
 * worker wakes `coro` by name if set, else signals that loop's cond (see rf_race_wait). */
void rf_task_race_register(rf_task* task, rf_sched* s, rf_coro* coro);
int rf_task_spawn_threaded(rf_task* task, rf_task_entry_fn entry, void* userdata);

void rf_task_mark_ready(rf_task* task);
void rf_task_mark_running(rf_task* task);
void rf_task_mark_parked(rf_task* task);

void rf_task_complete_value(rf_task* task, void* result_payload);
void rf_task_complete_error(rf_task* task, void* error_payload);
void rf_task_complete_cancelled(rf_task* task);
void rf_task_complete_timeout(rf_task* task);

void rf_task_request_cancel(rf_task* task);
bool rf_task_is_cancel_requested(rf_task* task);

/* Unified cooperative-cancellation poll for the agent (coroutine OR worker thread) running on this
 * OS thread: returns 1 if cancellation has been requested for it, else 0. Backs the stdlib
 * cancellation_requested(); reads only thread-local state, never frees or unwinds. */
uint32_t rf_cancel_requested(void);

void rf_task_mark_result_consumed(rf_task* task);
bool rf_task_is_result_consumed(rf_task* task);

void rf_task_attach_execution_backend(rf_task* task, void* backend);
void* rf_task_execution_backend(rf_task* task);
void rf_task_attach_wait_backend(rf_task* task, void* backend);
void* rf_task_wait_backend(rf_task* task);

void rf_task_add_prerequisite(rf_task* task);
void rf_task_add_dependent(rf_task* task, rf_task* dependent);
uint32_t rf_task_prerequisite_count(rf_task* task);
uint32_t rf_task_prerequisites_remaining(rf_task* task);
bool rf_task_prerequisite_complete(rf_task* task, bool success);

void rf_waitfor_duration(int64_t duration_seconds, uint32_t duration_nanoseconds);

/* ---------------------------------------------------------------------------
 * v0.2.0 stackful coroutine primitive (Phase 1: context-switch spike).
 * Single coroutine, no scheduler.
 * --------------------------------------------------------------------------- */
typedef enum rf_coro_status {
    RF_CORO_NEW = 0,        /* created, never resumed                        */
    RF_CORO_RUNNING = 1,    /* currently executing on its own stack          */
    RF_CORO_PARKED = 2,     /* yielded; resumable                            */
    RF_CORO_COMPLETED = 3,  /* entry returned; not resumable                 */
    RF_CORO_CANCELLED = 4   /* abandoned while parked; teardown thunks ran   */
} rf_coro_status;

/* The $destroy of one owned value, called on the value's address (its `me`). Member $destroy is
 * always passed `me` by pointer for every type, so a single signature covers all owned types — no
 * per-type thunk needed. On abandon it is the ONLY thing that frees the value; on normal exit the
 * inline $destroy runs instead and the node is popped without firing. (v0.2.0 Mechanism C.) */
typedef void (*rf_destroy_fn)(void* self);

/* One cancellation node in a coroutine's shadow stack — one per live owned value. The node lives
 * as a local in the scope that owns the value (intrusive, zero-allocation); push/pop only relink
 * the coroutine's top. Pushed when the value is constructed, popped at its inline $destroy. */
typedef struct rf_cancel_frame {
    void* value_ptr;                /* address of the owned value (passed as `me`)  */
    rf_destroy_fn destroy_fn;       /* that value's $destroy                         */
    struct rf_cancel_frame* prev;   /* next node down (older), or NULL at bottom     */
} rf_cancel_frame;

/* Allocate a coroutine whose body is entry(userdata). stack_size == 0 picks a default.
 * Returns NULL on allocation failure or NULL entry. Does NOT start running it. */
rf_coro* rf_coro_create(rf_context_entry_fn entry, void* userdata, size_t stack_size);

/* Switch into the coroutine; block the caller until it parks or finishes. Returns the
 * resulting status (RF_CORO_PARKED or RF_CORO_COMPLETED). Resuming a completed coroutine
 * is a no-op that returns RF_CORO_COMPLETED. */
rf_coro_status rf_coro_resume(rf_coro* coro);

/* Park the coroutine currently running on this OS thread, switching back to its resumer.
 * No-op when called outside any coroutine. */
void rf_coro_yield(void);

rf_coro_status rf_coro_status_get(rf_coro* coro);

/* Free the coroutine and its stack. Caller must not delete a coroutine that is currently
 * running, nor one parked with live cancellation frames — use rf_coro_abandon for that. */
void rf_coro_delete(rf_coro* coro);

/* Mark a coroutine as fire-and-forget (`execute()`): the worker that completes it frees it, no
 * consumer. Call BEFORE rf_sched_spawn_default so it is published before any worker runs it. */
void rf_coro_set_detached(rf_coro* coro);

/* Cancellation shadow stack (Phase 3). Push/pop are called by code running INSIDE the
 * coroutine (scope entry / normal scope exit); abandon is called by the host on a parked or
 * never-started coroutine. */

/* When an owned value is constructed: link `frame` (a caller-owned stack node) as the current
 * coroutine's new top, recording the value's address + its $destroy. No-op outside a coroutine. */
void rf_coro_cf_push(rf_cancel_frame* frame, void* value_ptr, rf_destroy_fn destroy_fn);

/* At the value's inline $destroy (normal/throw/break exit): unlink `frame` WITHOUT firing
 * (the inline $destroy already ran). `frame` must be the current top. No-op outside a coroutine. */
void rf_coro_cf_pop(rf_cancel_frame* frame);

/* Abandon a parked (or never-started) coroutine: walk its shadow stack top-to-bottom (reverse
 * construction order), call each live value's $destroy on its address exactly once, then free the
 * coroutine and its stack. Abandoning a completed coroutine just frees it (its nodes were popped). */
void rf_coro_abandon(rf_coro* coro);

/* The coroutine running on this OS thread, or NULL outside any coroutine. */
rf_coro* rf_coro_current(void);

/* Cooperative cancellation request for a coroutine (structured concurrency). request_cancel sets a
 * flag only — it never frees or unwinds; teardown stays in rf_coro_abandon at $destroy. The
 * coroutine observes the request at a suspend point (waitfor returns early) or via rf_cancel_requested
 * in a yield-free loop, and returns on its own. */
void rf_coro_request_cancel(rf_coro* coro);
uint32_t rf_coro_is_cancel_requested(rf_coro* coro);

/* ---------------------------------------------------------------------------
 * Single-thread cooperative scheduler (v0.2.0 async). Drives many coroutines on
 * one OS thread; a coroutine parks on a wake condition (today: a timer) and the
 * loop resumes it when ready, so `waitfor` parks instead of blocking the thread.
 * --------------------------------------------------------------------------- */

rf_sched* rf_sched_create(void);
void rf_sched_destroy(rf_sched* sched);

/* Queue a NEW coroutine to run; it starts on its first resume by the loop. */
void rf_sched_spawn(rf_sched* sched, rf_coro* coro);

/* Park the coroutine currently running under the loop for `delay_ns`, then resume it. Called from
 * inside a coroutine (e.g. by waitfor). No-op outside a running scheduler/coroutine. */
void rf_sched_park_timer(uint64_t delay_ns);

/* Park the current coroutine with no scheduler-satisfiable wake: only rf_sched_wake re-queues it.
 * How a coroutine awaits work on another OS thread without blocking the scheduler thread. */
void rf_sched_park_external(void);

/* Park the current coroutine until `delay_ns` from now, but ALSO leave it externally wakeable
 * (rf_sched_wake) — resumed whichever happens first. Substrate for a timed await (race a task's
 * completion against a deadline). An external wake unlinks it from the timer list. */
void rf_sched_park_deadline(uint64_t delay_ns);

/* The monotonic nanosecond clock the scheduler's timers use (for the timed-await bridge). */
uint64_t rf_monotonic_now_ns(void);

/* Make a parked coroutine runnable again. Safe to call from ANY thread — the bridge a worker
 * thread uses to hand a result back to a coroutine awaiting it on the scheduler thread. */
void rf_sched_wake(rf_sched* sched, rf_coro* coro);

/* Drive all spawned coroutines to completion on this thread (returns when none remain). */
void rf_sched_run(rf_sched* sched);

/* Drive the scheduler only until `target` finishes, leaving other coroutines parked. The engine
 * behind Coroutine[T].retrieve!() (run-until-this-handle semantics). */
void rf_sched_run_until(rf_sched* sched, rf_coro* target);

/* Signal a scheduler's run loop with no target coroutine (just its cond). The wake a worker thread
 * gives a `race!` loop on completing a competitor task. Safe to call from ANY thread. */
void rf_sched_signal(rf_sched* sched);

/* Cross-thread wake bookkeeping for the deadlock diagnostic. A coroutine that parks awaiting a wake
 * from ANOTHER thread — a threaded Task's completion, an async-I/O finish, a SignalCaster cast, or a
 * `race!` thread competitor — arms one of these before parking and disarms once the wake resolves;
 * the count is the number of such outstanding cross-thread wake promises. The run loop uses it to
 * tell a genuine all-coroutine deadlock (nothing runnable AND no cross-thread wake can ever arrive)
 * apart from a legitimate wait on another thread. Channel parks do NOT arm it: the RF-S632 entity
 * aliasing barrier keeps a channel's counterpart on the SAME scheduler, so a channel wake is never
 * cross-thread. Both are safe to call from any thread; disarm is clamped at zero. */
void rf_sched_arm_cross_waker(rf_sched* sched);
void rf_sched_disarm_cross_waker(rf_sched* sched);

/* ---- race!: drive a heterogeneous Agent set until the first competitor finishes -------------- */

/* An opaque competitor set built incrementally by the stdlib `race!`, one entry per Agent in List
 * order, then driven by rf_race_wait. */
typedef struct rf_race rf_race;

/* Allocate an empty competitor set. */
rf_race* rf_race_begin(void);
/* Append a coroutine competitor (an Agent of kind CORO). Order = List order. */
void rf_race_add_coro(rf_race* race, rf_coro* coro);
/* Append a thread competitor (an Agent of kind THREAD). Order = List order. */
void rf_race_add_task(rf_race* race, rf_task* task);
/* Drive this thread's implicit scheduler until one competitor completes; return its index in add
 * (List) order, or -1 if the set is empty. Registers each thread competitor so its completion wakes
 * the loop; the completion poll and the cond wait share the scheduler lock, so no wake is lost. */
intptr_t rf_race_wait(rf_race* race);
/* Free the set (does NOT touch the competitors themselves — the caller still owns the Agents). */
void rf_race_end(rf_race* race);

/* 1 if the caller runs inside a scheduler-driven coroutine (a park would suspend), else 0.
 * Lets `waitfor` park under a scheduler but OS-sleep on a plain thread. */
int rf_in_coroutine(void);

/* The scheduler currently driving this OS thread (inside run/run_until), or NULL. The task↔coro
 * await bridge reads it to learn which scheduler must wake the coroutine on completion. */
rf_sched* rf_sched_current(void);

/* ---- Implicit per-thread scheduler: the `retrieve!` entry into async --------------------- */

/* This thread's implicit scheduler, created lazily on first use and reused across calls. */
rf_sched* rf_sched_thread_default(void);

/* Spawn a NEW coroutine onto this thread's implicit scheduler (emitted at a suspended-routine call
 * site, so the coroutine is ready before any retrieve! drives the loop). */
void rf_sched_spawn_default(rf_coro* coro);

/* Drive this thread's implicit scheduler until `target` finishes (retrieve!'s engine). */
void rf_sched_run_until_default(rf_coro* target);

/* Detach `coro` from this thread's implicit scheduler if present (returns 1 if removed). Call
 * before rf_coro_abandon on a spawned-but-never-retrieved coroutine, so the scheduler drops its
 * reference + live count instead of dangling. */
int rf_sched_unschedule_default(rf_coro* coro);

/* Number of worker threads driving the process pool (fixed host-core-count, min 1). Creates the pool
 * on first call. Coroutine migration across workers can only occur when this is > 1. */
uint64_t rf_sched_worker_count(void);

/* ---- Channels: streaming conduit (`Hopper[T]` / `Conveyor[T]`) --------------------------- */

/* Refcounted ring buffer carrying payload pointers between agents. feed (full) and next (empty)
 * park the caller inside a coroutine (rf_sched_park_external + rf_sched_wake) or block it on a plain
 * thread — the same uncolored contract as retrieve!/waitfor. Backed by channel_runtime.c. */
typedef struct rf_channel rf_channel;

/* Create a channel. capacity = buffered slots; 0 = rendezvous (feed waits for a taker). Throws
 * OutOfMemoryError on a capacity*sizeof(slot) overflow or allocation failure. Starts with one
 * producer ref + one consumer ref (the Feeder + receiver returned by make_*). */
rf_channel* rf_channel_create(uint64_t capacity);

/* Handle refcounting: a clone adds a ref on its side; a drop releases one. Last Feeder drop
 * auto-closes; the struct frees when both producer and consumer refs reach 0. */
void rf_channel_add_feeder(rf_channel* chan);
void rf_channel_drop_feeder(rf_channel* chan);
void rf_channel_add_consumer(rf_channel* chan);
void rf_channel_drop_consumer(rf_channel* chan);

/* Send: 1 on success, 0 if closed or consumer-less (RF lowers 0 to a failable throw). Backpressure:
 * blocks/parks while full. Receive: returns a payload, or NULL when closed AND drained. */
uint32_t rf_channel_feed(rf_channel* chan, void* payload);
void* rf_channel_next(rf_channel* chan);

/* Explicit early close (drop auto-closes too). Not dangerous; double-close is a no-op. */
void rf_channel_close(rf_channel* chan);

/* Introspection snapshots (racy by nature, not synchronization primitives): buffered item count,
 * whether the channel is closed, and the buffered capacity it was created with (0 = rendezvous). */
uint64_t rf_channel_count(rf_channel* chan);
uint32_t rf_channel_is_closed(rf_channel* chan);
uint64_t rf_channel_capacity(rf_channel* chan);

/* ---- SignalCaster: a condition-variable monitor ----------------------------------------- */

/* A self-contained monitor (internal mutex + wait set). wait is UNCOLORED: it parks a coroutine
 * (rf_sched_park_external + rf_sched_wake) or blocks a plain thread on the internal condvar, releasing
 * and re-acquiring the monitor lock around the suspend. Refcounted so it can be shared across agents.
 * Backed by signal_runtime.c. */
typedef struct rf_signal rf_signal;

/* Create a monitor (refcount 1). Throws OutOfMemoryError on allocation failure. */
rf_signal* rf_signal_create(void);
/* Handle refcounting: clone adds a ref, drop releases one; the struct frees at 0. */
void rf_signal_add_ref(rf_signal* sig);
void rf_signal_drop(rf_signal* sig);

/* Acquire / release the monitor lock that guards the caller's shared predicate. */
void rf_signal_lock(rf_signal* sig);
void rf_signal_unlock(rf_signal* sig);

/* Wait for a cast — MUST hold the monitor lock. Atomically drops the lock, suspends (coroutine park
 * or thread condvar), and re-acquires the lock before returning. Callers re-check the predicate in a
 * loop. This is a suspend primitive (see SuspendPrimitives in MaySuspendAnalysis). */
void rf_signal_wait(rf_signal* sig);

/* Timed wait — like rf_signal_wait but bounded by timeout_ns. Returns 1 if woken before the deadline
 * (re-check the predicate), 0 if the deadline elapsed. Also a suspend primitive. */
uint32_t rf_signal_wait_deadline(rf_signal* sig, uint64_t timeout_ns);

/* Wake one / all waiter(s). Call after updating the predicate (typically just after unlock), NOT while
 * holding the monitor lock — these acquire it internally. */
void rf_signal_cast_one(rf_signal* sig);
void rf_signal_cast_all(rf_signal* sig);

#ifdef __cplusplus
}
#endif

#endif
