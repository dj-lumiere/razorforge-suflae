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

const char* rf_task_kind_name(rf_task_kind kind);
const char* rf_task_status_name(rf_task_status status);
const char* rf_task_completion_name(rf_task_completion_kind kind);

rf_task* rf_task_create(rf_task_kind kind);
void rf_task_destroy(rf_task* task);

uint64_t rf_task_id(rf_task* task);

/* Opaque, stable identifier for the calling OS thread. Used by the lock policies to detect a
 * re-entrant claim (a thread acquiring an exclusive lock it already holds = self-deadlock). */
uint64_t rf_current_thread_id(void);
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
 * Single coroutine, no scheduler. See internal-wiki/v0.2.0-coroutine-primitive.md.
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

/* ---------------------------------------------------------------------------
 * Single-thread cooperative scheduler (v0.2.0 async). Drives many coroutines on
 * one OS thread; a coroutine parks on a wake condition (today: a timer) and the
 * loop resumes it when ready, so `waitfor` parks instead of blocking the thread.
 * --------------------------------------------------------------------------- */
typedef struct rf_sched rf_sched;

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

/* Make a parked coroutine runnable again. Safe to call from ANY thread — the bridge a worker
 * thread uses to hand a result back to a coroutine awaiting it on the scheduler thread. */
void rf_sched_wake(rf_sched* sched, rf_coro* coro);

/* Drive all spawned coroutines to completion on this thread (returns when none remain). */
void rf_sched_run(rf_sched* sched);

/* Drive the scheduler only until `target` finishes, leaving other coroutines parked. The engine
 * behind Coroutine[T].retrieve!() (run-until-this-handle semantics). */
void rf_sched_run_until(rf_sched* sched, rf_coro* target);

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

#ifdef __cplusplus
}
#endif

#endif
