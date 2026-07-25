/*
 * coro_runtime.c — v0.2.0 stackful coroutine primitive (Phase 1: context-switch spike).
 *
 * Thin, ABI-neutral wrapper over the vendored libco context switch. Exposes the four
 * substrate primitives the v0.2.0 design (§8)
 * names: create / resume / yield / delete. The cancellation shadow stack + rf_coro_abandon
 * (Phase 3) and the compiler instrumentation (Phases 4-5) build ON TOP of this; they are
 * deliberately NOT here yet.
 *
 * Model (single coroutine, no scheduler):
 *   - rf_coro_resume() switches INTO a coroutine and blocks the caller until that coroutine
 *     parks (rf_coro_yield) or finishes (entry returns). It returns the resulting status.
 *   - rf_coro_yield() switches back OUT to whoever most recently resumed this coroutine.
 *   - libco runs the coroutine on the same OS thread, so the "currently running coroutine"
 *     pointer is per-OS-thread (_Thread_local). It survives the stack swap because it lives
 *     in thread-local storage, not on either stack.
 */

#include "../include/razorforge_runtime.h"

#include <stdatomic.h> /* atomic sched_state — a worker drains/steals a coroutine off s->lock (step 3) */
#include <stdlib.h>

#ifdef _WIN32
#include <windows.h>
#include <process.h> /* _beginthreadex — pool worker threads */
#else
#include <time.h>
#include <errno.h>
#include <unistd.h> // sysconf(_SC_NPROCESSORS_ONLN) — pool worker count
#endif

#ifdef HAVE_LIBCO
#include "libco.h"
#endif

/* A coroutine context-switch backend is available whenever we can run coroutines at all: native
 * Windows fibers on _WIN32, libco elsewhere. The SHARED coroutine machinery (cancellation frames,
 * abandon, rf_coro_current, the cooperative yield) gates on THIS, not on HAVE_LIBCO — so it never
 * depends on libco being linked on Windows, where libco is unused (the backend is fibers). The
 * backend-SPECIFIC parts still gate on `_WIN32` (fibers) vs `HAVE_LIBCO && !_WIN32` (libco). */
#if defined(_WIN32) || defined(HAVE_LIBCO)
  #define RF_HAVE_CORO 1
#endif

#include "rf_sync.h" /* portable rf_mutex / rf_cond — shared with the task↔coro bridge */

/* Runtime error + stack trace + exit(1) (stacktrace.c). We raise this on a hard allocation failure so
 * a coroutine that cannot be created dies with a diagnosed error instead of a NULL that crashes later
 * (or, at scale, a machine wedged under commit pressure). */
extern void __rf_throw(const char* error_type, const char* message);

/* Per-coroutine shadow-stack handoff (stacktrace.c). A coroutine owns its own RF-level call-chain
 * shadow stack so a stack trace stays correct when the M:N scheduler migrates it between OS workers.
 * create returns an opaque handle (NULL when tracing is off — then the thread default is used, and
 * is never pushed to); activate swaps it in on resume and returns the previous handle to restore on
 * switch-out; destroy frees it at teardown. */
extern void* __rf_stack_coro_create(void);
extern void  __rf_stack_coro_destroy(void* handle);
extern void* __rf_stack_activate(void* handle);

/* Default coroutine stack reserve, in bytes, when the caller passes stack_size == 0. This is a
 * VIRTUAL reserve, not committed up front: pages back the stack only as it actually grows into them,
 * so a parked shallow coroutine charges roughly the pages it has touched — NOT a full megabyte —
 * letting a great many coroutines coexist. The demand growth is the OS's job on both backends:
 * Windows fibers (CreateFiberEx: small commit + this reserve, guard-page growth) and POSIX libco
 * stacks (mmap MAP_NORESERVE + a no-access guard page; see rf_coro_stack_alloc). A deep call chain is
 * therefore safe — the stack grows on demand up to this reserve. (Design §9.2.) */
#define RF_CORO_DEFAULT_STACK (1024u * 1024u)

// Per-coroutine scheduling state — the worker-safe park/wake state machine (M:N build step 2b). With
// N>1 workers, two threads can try to make the same coroutine runnable at once: a wake (from another
// worker or a task thread) racing the worker that is STILL running it. Only one owner may ever push a
// coroutine onto a run queue, so every "make runnable" goes through rf_sched_make_ready under s->lock
// and is a pure function of this state:
//   IDLE     — parked (external wait, or on the timer list) or freshly created; not queued, not running
//   QUEUED   — sitting in the injector, waiting to be popped; a wake is a no-op (already runnable)
//   RUNNING  — a worker popped it and is executing it (s->lock dropped); a wake records NOTIFIED
//              instead of enqueuing — the running worker re-queues it when it parks
//   NOTIFIED — was RUNNING when a wake arrived; the worker re-queues it on park, IGNORING its park
//              intent (the wake means "become runnable again")
//   DONE     — completed; never runnable again (a stray wake is dropped)
// This replaces the old single `in_ready` flag (which was just "state == QUEUED" and could not tell a
// parked coroutine apart from a running one — the exact ambiguity that lets N>1 double-resume).
typedef enum {
    RF_SCHED_IDLE = 0,
    RF_SCHED_QUEUED,
    RF_SCHED_RUNNING,
    RF_SCHED_NOTIFIED,
    RF_SCHED_DONE
} rf_sched_state;

// What a coroutine asked its worker to do when it switched out. The park primitives run INSIDE the
// coroutine (on the worker) and only RECORD an intent — they do NOT touch the injector/timer list
// themselves. The worker applies the intent AFTER rf_coro_resume returns, under s->lock, so a wake
// that arrived meanwhile (state == NOTIFIED) cleanly overrides it (requeue instead of park). At N=1
// this was safe to do inline; at N>1 the record-then-apply split is what closes the wake-vs-park race.
typedef enum {
    RF_PARK_NONE = 0,  // not parking (a completion, or nothing pending)
    RF_PARK_TIMER,     // insert into the timer list at wake_ns
    RF_PARK_EXTERNAL,  // leave IDLE in no list; only rf_sched_wake re-queues it
    RF_PARK_YIELD      // cooperative yield: re-queue immediately, behind the other ready coros
} rf_park_intent;

struct rf_coro {
    rf_context_entry_fn entry;     /* user routine to run inside the coroutine            */
    void* userdata;                /* opaque argument handed to entry                     */
    rf_coro_status status;         /* NEW -> RUNNING -> {PARKED -> RUNNING}* -> COMPLETED */
    rf_cancel_frame* cf_top;       /* top of the cancellation shadow stack (NULL = empty) */
    struct rf_coro* sched_next;    /* injector FIFO link (the timer structure is now a heap, below) */
    uint64_t wake_ns;              /* monotonic deadline this coroutine is parked until         */
    int timer_idx;                 /* position in the scheduler's timer min-heap, or -1 if not on it
                                    * (step 5). Lets an early wake remove it in O(log n).       */
    _Atomic rf_sched_state sched_state; // worker-safe park/wake state (M:N step 2b); ATOMIC as of step
                                   // 3 so a worker's QUEUED->RUNNING pop/steal (under a deque lock, NOT
                                   // s->lock) is race-free against a make_ready reading it under s->lock.
                                   // Every access goes through atomic_load/atomic_store (seq_cst).
    rf_park_intent park_intent;    // recorded by a park primitive; applied by the worker post-resume
    int counted_done;              /* 1 once its completion has decremented live (idempotent)   */
    int cancel_requested;          /* 1 once cooperative cancellation has been requested        */
    struct rf_coro* awaiter;       /* a coroutine parked in retrieve! awaiting THIS one's completion;
                                    * the pool worker wakes it (pushes to the injector) when this
                                    * coroutine completes. NULL = no coroutine is awaiting. Single
                                    * slot: retrieve! is 1:1 per Agent handle (race! sets+clears it
                                    * across a competitor set, tolerating spurious wakes).          */
    void* shadow_stack;            /* this coroutine's RF-level call-chain shadow stack (migrates
                                    * with it across workers); NULL when tracing is off         */
#if defined(_WIN32)
    void* fiber;                   /* Windows fiber backing this coroutine (CreateFiberEx)        */
    void* resumer_fiber;           /* fiber to switch back to on yield/finish                     */
#elif defined(HAVE_LIBCO)
    cothread_t thread;             /* libco context for this coroutine's own stack        */
    cothread_t resumer;            /* context to switch back to on yield/finish           */
    void* stack_region;            /* whole stack mapping (guard page included) for teardown     */
    size_t stack_region_size;      /* byte length of stack_region; 0 if libco malloc'd it itself */
#endif
};

/* The coroutine currently executing on THIS OS thread. NULL when running ordinary (non-coroutine)
 * code. Set by resume immediately before switching in so the trampoline, yield, cf_push/pop,
 * rf_coro_current, and the scheduler can recover their own rf_coro*. Thread-local: each OS thread
 * drives its own coroutines independently. Needed by BOTH the Windows fiber and libco backends. */
static _Thread_local rf_coro* g_current_coro = NULL;

#if defined(_WIN32)
/* ---- Windows backend: native fibers ----------------------------------------------------------
 * A coroutine is a Windows fiber (CreateFiberEx) with a small initial commit and a large reserve.
 * Windows manages the fiber's stack like a thread stack: cheap up front (so very many coroutines
 * coexist) and demand-paged GUARD-PAGE GROWTH on deep calls — so ANY normal routine, including deep
 * C-runtime calls like fopen, works inside a coroutine. This replaces the VEH-demand-committed libco
 * stacks used on POSIX, whose user-mode fault handler could not run once a single large stack-pointer
 * drop (fopen's path buffer) exhausted the committed region. */
#include <windows.h>

static _Thread_local int g_thread_is_fiber = 0;

/* Make THIS OS thread a fiber so SwitchToFiber works. Idempotent: if the thread is already a fiber,
 * ConvertThreadToFiber fails with ERROR_ALREADY_FIBER, which is fine — we only need to BE one. */
static void rf_coro_ensure_thread_is_fiber(void)
{
    if (!g_thread_is_fiber) {
        ConvertThreadToFiber(NULL);
        g_thread_is_fiber = 1;
    }
}

/* Fiber bootstrap (the CreateFiberEx start routine). Runs the user entry to completion on the
 * fiber's own Windows-managed stack, marks COMPLETED, then switches back to the resumer. It must
 * NEVER return — a fiber proc that returns terminates the whole OS thread — so the trailing
 * SwitchToFiber does not return: a COMPLETED coroutine is never resumed again, and $destroy deletes
 * the fiber. `self` comes from the fiber parameter. */
static void __stdcall rf_coro_fiber_proc(void* param)
{
    rf_coro* self = (rf_coro*)param;
    self->entry(self->userdata);
    self->status = RF_CORO_COMPLETED;
    SwitchToFiber(self->resumer_fiber);
}
#endif /* _WIN32 fiber backend */

#if defined(HAVE_LIBCO) && !defined(_WIN32)

/* Platform memory primitives for demand-paged coroutine stacks (rf_coro_stack_alloc). POSIX only —
 * Windows uses fibers above. */
#include <sys/mman.h>
#include <unistd.h>

/* libco bootstrap. Runs on the coroutine's own stack the first time it is resumed. Recovers
 * `self` from the thread-local that resume just set, runs the user entry to completion, marks
 * COMPLETED, and switches back to the resumer. Control never falls off the end of this
 * function: the final co_switch does not return. */
static void rf_coro_trampoline(void)
{
    rf_coro* self = g_current_coro;
    self->entry(self->userdata);
    self->status = RF_CORO_COMPLETED;
    co_switch(self->resumer);
}

/* Allocate a demand-paged coroutine stack of (at least) `usable` bytes and hand back the usable
 * region to give co_derive. POSIX only — Windows uses native fibers (the OS manages their stacks).
 * A no-access GUARD PAGE sits just below the usable region (the stack grows downward into it), so a
 * stack overflow faults cleanly instead of silently scribbling on the neighbouring allocation. The
 * mapping is MAP_NORESERVE, so the kernel commits pages only as the stack actually touches them and a
 * generous reserve stays cheap. *region / *region_size capture the WHOLE mapping (guard included) for
 * rf_coro_stack_free. Returns NULL on failure (the caller raises a runtime error). */
static void* rf_coro_stack_alloc(size_t usable, void** region, size_t* region_size)
{
    long pgl = sysconf(_SC_PAGESIZE);
    size_t pg = (pgl > 0) ? (size_t)pgl : 4096u;
    size_t guard = pg;
    size_t body = (usable + pg - 1) & ~(pg - 1);
    size_t total = guard + body;
    /* MAP_NORESERVE: do not reserve swap up front; pages commit on first touch (demand-paged). Keeps
     * a generous reserve cheap so many coroutines can coexist. (max_map_count remains the kernel-side
     * ceiling on the number of mappings; mmap returns MAP_FAILED past it → NULL → caller throws.) */
#ifndef MAP_NORESERVE
#define MAP_NORESERVE 0
#endif
    void* base = mmap(NULL, total, PROT_READ | PROT_WRITE,
                      MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    if (base == MAP_FAILED) {
        return NULL;
    }
    if (mprotect(base, guard, PROT_NONE) != 0) {
        munmap(base, total);
        return NULL;
    }
    *region = base;
    *region_size = total;
    return (char*)base + guard;
}

/* Release a stack mapping from rf_coro_stack_alloc. NOT co_delete: that frees the libco handle with
 * LIBCO_FREE (plain free), but our stack is an mmap region, not malloc'd. */
static void rf_coro_stack_free(void* region, size_t region_size)
{
    if (region == NULL) {
        return;
    }
    munmap(region, region_size);
}
#endif /* HAVE_LIBCO && !_WIN32 */

rf_coro* rf_coro_create(rf_context_entry_fn entry, void* userdata, size_t stack_size)
{
    if (entry == NULL) {
        return NULL;
    }

    rf_coro* coro = (rf_coro*)calloc(1, sizeof(rf_coro));
    if (coro == NULL) {
        __rf_throw("OutOfMemoryError", "Failed to allocate coroutine control block");
        return NULL; /* unreachable: __rf_throw exits */
    }

    coro->entry = entry;
    coro->userdata = userdata;
    coro->status = RF_CORO_NEW;
    coro->timer_idx = -1; /* not on any timer heap yet (calloc's 0 would alias heap slot 0) */
    coro->shadow_stack = __rf_stack_coro_create(); /* NULL when tracing is off */

#if defined(_WIN32)
    /* Fiber with a small initial commit + large reserve: cheap per coroutine, and Windows grows the
     * stack on demand (guard-page growth) so deep native calls inside the coroutine are safe.
     * FIBER_FLAG_FLOAT_SWITCH preserves x87/SSE state across switches (required for correctness). */
    SIZE_T reserve = (stack_size == 0) ? (SIZE_T)RF_CORO_DEFAULT_STACK : (SIZE_T)stack_size;
    SIZE_T commit = 4u * 1024u;
    coro->fiber = CreateFiberEx(commit, reserve, FIBER_FLAG_FLOAT_SWITCH, rf_coro_fiber_proc, coro);
    if (coro->fiber == NULL) {
        free(coro);
        __rf_throw("OutOfMemoryError", "Failed to create coroutine fiber");
        return NULL; /* unreachable */
    }
#elif defined(HAVE_LIBCO)
    size_t reserve = (stack_size == 0) ? (size_t)RF_CORO_DEFAULT_STACK : stack_size;
    void* region;
    size_t region_size;
    void* usable = rf_coro_stack_alloc(reserve, &region, &region_size);
    if (usable == NULL) {
        free(coro);
        __rf_throw("OutOfMemoryError",
                   "Failed to reserve coroutine stack (out of address space or commit limit)");
        return NULL; /* unreachable */
    }
    /* Size given to co_derive = the usable body (whole mapping minus the leading header+guard). */
    size_t body = region_size - (size_t)((char*)usable - (char*)region);
    coro->thread = co_derive(usable, (unsigned int)body, rf_coro_trampoline);
    if (coro->thread == NULL) {
        rf_coro_stack_free(region, region_size);
        free(coro);
        __rf_throw("OutOfMemoryError", "Failed to derive coroutine context");
        return NULL; /* unreachable */
    }
    coro->stack_region = region;
    coro->stack_region_size = region_size;
#else
    (void)stack_size;
#endif

    return coro;
}

rf_coro_status rf_coro_resume(rf_coro* coro)
{
    if (coro == NULL) {
        return RF_CORO_COMPLETED;
    }
    if (coro->status == RF_CORO_COMPLETED) {
        return RF_CORO_COMPLETED;
    }

#if defined(_WIN32)
    rf_coro_ensure_thread_is_fiber();      /* this OS thread must be a fiber to SwitchToFiber */
    coro->resumer_fiber = GetCurrentFiber();
    rf_coro* prev = g_current_coro;
    g_current_coro = coro;
    void* prev_shadow = __rf_stack_activate(coro->shadow_stack); /* call chain follows the coroutine */
    coro->status = RF_CORO_RUNNING;

    SwitchToFiber(coro->fiber); /* runs fiber_proc (first time) or returns from yield */

    __rf_stack_activate(prev_shadow); /* restore the resumer's call chain */
    g_current_coro = prev;   /* the coroutine parked or finished; restore our context */
    return coro->status;     /* PARKED (yielded) or COMPLETED (entry returned)        */
#elif defined(HAVE_LIBCO)
    coro->resumer = co_active();
    rf_coro* prev = g_current_coro;
    g_current_coro = coro;
    void* prev_shadow = __rf_stack_activate(coro->shadow_stack); /* call chain follows the coroutine */
    coro->status = RF_CORO_RUNNING;

    co_switch(coro->thread); /* runs trampoline (first time) or returns from yield */

    __rf_stack_activate(prev_shadow); /* restore the resumer's call chain */
    g_current_coro = prev;   /* the coroutine parked or finished; restore our context */
    return coro->status;     /* PARKED (yielded) or COMPLETED (entry returned)        */
#else
    /* No context-switch backend: degrade to a synchronous run-to-completion so callers
     * still make progress (yield becomes a no-op). */
    coro->status = RF_CORO_RUNNING;
    coro->entry(coro->userdata);
    coro->status = RF_CORO_COMPLETED;
    return RF_CORO_COMPLETED;
#endif
}

/* Pure context switch back to the resumer, marking PARKED. The low-level half of every suspend:
 * the caller is responsible for arranging WHEN the coroutine becomes runnable again (a timer, an
 * external wake, or re-queuing to ready for a cooperative yield). NOT scheduler-aware itself.
 * The public rf_coro_yield (cooperative) is defined after the scheduler, since it re-queues. */
static void rf_coro_switch_out(void)
{
#if defined(_WIN32)
    rf_coro* self = g_current_coro;
    if (self == NULL) {
        return; /* not inside a coroutine — yielding the OS thread is meaningless here */
    }
    self->status = RF_CORO_PARKED;
    SwitchToFiber(self->resumer_fiber);
    /* Resumed: resume() has already set status back to RUNNING and g_current_coro to self. */
#elif defined(HAVE_LIBCO)
    rf_coro* self = g_current_coro;
    if (self == NULL) {
        return; /* not inside a coroutine — yielding the OS thread is meaningless here */
    }
    self->status = RF_CORO_PARKED;
    co_switch(self->resumer);
    /* Resumed: resume() has already set status back to RUNNING and g_current_coro to self. */
#endif
}

rf_coro_status rf_coro_status_get(rf_coro* coro)
{
    if (coro == NULL) {
        return RF_CORO_COMPLETED;
    }
    return coro->status;
}

/* ---- Cooperative cancellation request (structured concurrency) ---------------------------- */
/* These NEVER free or unwind — they only set/read a flag. Actual teardown stays exclusively in
 * the host's rf_coro_abandon / rf_task join at $destroy (the single-freer invariant). A coroutine
 * observes the request at a suspend point (waitfor returns early) or by polling rf_cancel_requested
 * in a yield-free loop, and returns on its own. */
void rf_coro_request_cancel(rf_coro* coro)
{
    if (coro == NULL) {
        return;
    }
    coro->cancel_requested = 1;
}

uint32_t rf_coro_is_cancel_requested(rf_coro* coro)
{
    if (coro == NULL) {
        return 0u;
    }
    return coro->cancel_requested ? 1u : 0u;
}

void rf_coro_delete(rf_coro* coro)
{
    if (coro == NULL) {
        return;
    }
#if defined(_WIN32)
    if (coro->fiber != NULL) {
        DeleteFiber(coro->fiber); /* frees the Windows-managed fiber stack */
    }
#elif defined(HAVE_LIBCO)
    if (coro->thread != NULL) {
        /* Release the stack mapping ourselves (guard page included). We must NOT co_delete it:
         * co_delete frees the handle with plain free(), but our stack came from mmap/VirtualAlloc. */
        rf_coro_stack_free(coro->stack_region, coro->stack_region_size);
    }
#endif
    __rf_stack_coro_destroy(coro->shadow_stack); /* free the migrating call-chain stack */
    free(coro);
}

/* ---- Cancellation shadow stack (Phase 3) -------------------------------------------------- */

void rf_coro_cf_push(rf_cancel_frame* frame, void* value_ptr, rf_destroy_fn destroy_fn)
{
    if (frame == NULL) {
        return;
    }
#ifdef RF_HAVE_CORO
    rf_coro* self = g_current_coro;
    if (self == NULL) {
        return; /* not inside a coroutine: nothing to abandon, so nothing to track */
    }
    frame->value_ptr = value_ptr;
    frame->destroy_fn = destroy_fn;
    frame->prev = self->cf_top;
    self->cf_top = frame;
#else
    (void)value_ptr;
    (void)destroy_fn;
#endif
}

void rf_coro_cf_pop(rf_cancel_frame* frame)
{
#ifdef RF_HAVE_CORO
    rf_coro* self = g_current_coro;
    if (self == NULL || self->cf_top != frame) {
        return; /* unbalanced pop (or outside a coroutine): leave the stack intact */
    }
    self->cf_top = frame->prev;
#else
    (void)frame;
#endif
}

void rf_coro_abandon(rf_coro* coro)
{
    if (coro == NULL) {
        return;
    }

    /* A completed coroutine ran every inline destroy and popped every node already; abandon
     * degenerates to a plain free. Anything else (NEW or PARKED) walks whatever nodes are
     * live, calling each owned value's $destroy on its address (passed as `me`). Abandon is
     * only ever at a suspend point, so no value can have both its inline destroy and its node's
     * destroy run — the double-free invariant (design §7.6). Top-to-bottom = reverse
     * construction order = correct teardown order. */
    if (coro->status != RF_CORO_COMPLETED) {
        coro->status = RF_CORO_CANCELLED;
        rf_cancel_frame* frame = coro->cf_top;
        while (frame != NULL) {
            rf_cancel_frame* prev = frame->prev; /* read before the destroy runs */
            if (frame->destroy_fn != NULL) {
                frame->destroy_fn(frame->value_ptr);
            }
            frame = prev;
        }
        coro->cf_top = NULL;
    }

    rf_coro_delete(coro);
}

/* The coroutine running on this OS thread, or NULL outside any coroutine. Exposes the
 * backend-agnostic g_current_coro so the scheduler and the task↔coro await bridge (task_runtime.c)
 * can recover the running coroutine regardless of which backend (fiber or libco) is in use. */
rf_coro* rf_coro_current(void)
{
#ifdef RF_HAVE_CORO
    return g_current_coro;
#else
    return NULL;
#endif
}

/* ---- Cooperative scheduler pool (M:N build step 1 — N=1 skeleton) -------------------------- */
/*
 * A run loop that drives many coroutines. A coroutine parks itself on a wake condition (today: a
 * monotonic timer, via rf_sched_park_timer) and yields back to the loop; the loop resumes it when
 * the condition is met. This is what makes `waitfor` inside a coroutine park (cheap) instead of
 * blocking the thread, and lets spawned coroutines progress concurrently.
 *
 * M:N SKELETON (internal-wiki/v0.3.x-mn-scheduler.md §8 step 1). `struct rf_sched` is being
 * reframed as the process's scheduler POOL; the single ready FIFO is renamed the INJECTOR — the
 * shared submission queue that, once N>1 workers exist (step 2), every worker drains. Today there
 * is exactly ONE worker (the thread inside rf_sched_run / run_until), so this is a pure rename with
 * IDENTICAL behavior — the injector is drained by one worker in FIFO order, as the ready FIFO was.
 * Per-worker local deques + work-stealing arrive in step 3; the injector stays the only ready queue
 * through step 2. The `rf_sched` typedef and every public `rf_sched_*` entry point are preserved so
 * codegen and the task↔coro bridge are untouched.
 */

/* ---- Per-worker local run deque (M:N build step 3) ------------------------------------------- */
/*
 * Each pool worker owns one deque of runnable coroutines. The OWNER pushes and pops at the BOTTOM
 * (LIFO — the freshest child is cache-hot and should run next); OTHER workers STEAL from the TOP
 * (FIFO — the oldest work, least likely to still be hot for its parent). This replaces the single
 * shared injector as the common case, so N workers no longer serialize on one global lock.
 *
 * Step 3 starts with a LOCKED RING (one mutex per deque), not a lock-free Chase-Lev deque: the
 * correctness risk in this step is the make_ready routing + worker-loop lock restructuring + the
 * QUEUED->RUNNING steal handoff, NOT the deque internals — and a locked ring already confines
 * contention to {owner, its current thief} instead of the one global injector lock. The container
 * ops are kept behind this small interface (push_bottom / pop_bottom / steal_top / remove) so the
 * internals can later be swapped for Chase-Lev without touching the scheduler.
 *
 * The ops are pure container operations — they do NOT touch sched_state. The QUEUED/RUNNING state
 * transitions stay in the scheduler layer (rf_sched_make_ready / the pop path), exactly as for the
 * injector, so the worker-safe state machine has a single home.
 *
 * LOCK ORDERING (load-bearing): a deque lock is only ever taken with s->lock already held (enqueue via
 * make_ready/spawn, rf_sched_remove) or with NO scheduler lock held (a worker draining its own deque or
 * stealing a victim's). Two deque locks are NEVER held at once. This is what keeps stealing deadlock-free.
 */
typedef struct rf_deque {
    rf_coro** buf;   /* ring buffer of coroutine pointers; cap is a power of two   */
    size_t cap;      /* capacity (power of two, so an index masks with a bitwise AND) */
    size_t bottom;   /* owner end: next free push slot (pop reads bottom-1). Monotonic; mask on use. */
    size_t top;      /* steal end: oldest queued slot. Monotonic; mask on use.    */
    rf_mutex lock;   /* guards this deque only. Never held together with another deque lock. */
} rf_deque;

#define RF_DEQUE_INIT_CAP 64u

static void rf_deque_init(rf_deque* d)
{
    d->cap = RF_DEQUE_INIT_CAP;
    d->buf = (rf_coro**)calloc(d->cap, sizeof(rf_coro*));
    if (d->buf == NULL) {
        __rf_throw("OutOfMemoryError", "Failed to allocate worker run deque");
        return; /* unreachable */
    }
    d->bottom = 0;
    d->top = 0;
    rf_mutex_init(&d->lock);
}

/* Owner-end push (LIFO). Grows (doubles) the ring when full, re-laying the live entries [top, bottom)
 * at fresh dense indices. Caller sets sched_state (QUEUED) — this only stores the pointer. */
static void rf_deque_push_bottom(rf_deque* d, rf_coro* c)
{
    rf_mutex_lock(&d->lock);
    if (d->bottom - d->top == d->cap) {
        size_t n = d->cap;
        size_t ncap = n * 2u;
        rf_coro** nb = (rf_coro**)calloc(ncap, sizeof(rf_coro*));
        if (nb == NULL) {
            rf_mutex_unlock(&d->lock);
            __rf_throw("OutOfMemoryError", "Failed to grow worker run deque");
            return; /* unreachable */
        }
        for (size_t i = 0; i < n; i++) {
            nb[i] = d->buf[(d->top + i) & (d->cap - 1u)];
        }
        free(d->buf);
        d->buf = nb;
        d->cap = ncap;
        d->top = 0;
        d->bottom = n;
    }
    d->buf[d->bottom & (d->cap - 1u)] = c;
    d->bottom++;
    rf_mutex_unlock(&d->lock);
}

/* Owner-end pop (LIFO): the most recently pushed coroutine, or NULL if empty. */
static rf_coro* rf_deque_pop_bottom(rf_deque* d)
{
    rf_coro* c = NULL;
    rf_mutex_lock(&d->lock);
    if (d->bottom != d->top) {
        d->bottom--;
        c = d->buf[d->bottom & (d->cap - 1u)];
        d->buf[d->bottom & (d->cap - 1u)] = NULL;
    }
    rf_mutex_unlock(&d->lock);
    return c;
}

/* Steal-end pop (FIFO): the oldest queued coroutine, or NULL if empty. Called by a DIFFERENT worker
 * than the owner; the per-deque lock serializes it against the owner's push/pop (so a one-element
 * deque can never hand the same coroutine to both — the QUEUED->RUNNING handoff stays exclusive). */
static rf_coro* rf_deque_steal_top(rf_deque* d)
{
    rf_coro* c = NULL;
    rf_mutex_lock(&d->lock);
    if (d->bottom != d->top) {
        c = d->buf[d->top & (d->cap - 1u)];
        d->buf[d->top & (d->cap - 1u)] = NULL;
        d->top++;
    }
    rf_mutex_unlock(&d->lock);
    return c;
}

/* Remove an arbitrary coroutine from the deque if present (compacting the hole), returns 1 if found.
 * O(n) and rare — only $destroy on a spawned-but-unfinished coroutine (rf_sched_remove) walks here. */
static int rf_deque_remove(rf_deque* d, rf_coro* c)
{
    int found = 0;
    rf_mutex_lock(&d->lock);
    size_t n = d->bottom - d->top;
    for (size_t i = 0; i < n; i++) {
        if (d->buf[(d->top + i) & (d->cap - 1u)] == c) {
            /* shift [i+1, n) one slot toward top, then drop the last (now duplicate) slot */
            for (size_t j = i; j + 1u < n; j++) {
                d->buf[(d->top + j) & (d->cap - 1u)] = d->buf[(d->top + j + 1u) & (d->cap - 1u)];
            }
            d->bottom--;
            d->buf[d->bottom & (d->cap - 1u)] = NULL;
            found = 1;
            break;
        }
    }
    rf_mutex_unlock(&d->lock);
    return found;
}

struct rf_sched {
    rf_coro* injector_head; /* shared submission FIFO — coroutines ready to resume now. At N=1 the
                             * single worker drains this; at N>1 (step 2) all workers pull from it. */
    rf_coro* injector_tail;
    rf_coro** timer_heap;  /* min-heap (by wake_ns) of coroutines parked on a deadline (step 5);
                            * timer_heap[0] is the earliest. Grown on demand; NULL until first use. */
    int timer_count;       /* number of coroutines currently on the timer heap                  */
    int timer_cap;         /* allocated capacity of timer_heap                                  */
    int live;              /* coroutines spawned but not yet completed          */
    int cross_wakers;      /* outstanding promises that ANOTHER thread will wake this loop
                            * (threaded await / async I/O / signal cast / race competitor). Lets
                            * the run loop tell a real all-coroutine deadlock apart from a
                            * legitimate cross-thread wait — see rf_sched_arm_cross_waker. */
    int waiters;           /* threads/coroutines currently BLOCKED awaiting a target's completion
                            * (retrieve! / race!). The always-running pool worker (step 2a) must only
                            * flag a deadlock when someone is actually stuck waiting: a background
                            * coroutine parked with no waker while `waiters == 0` is merely idle
                            * (pending teardown), NOT a deadlock. Incremented around each blocking
                            * wait in rf_sched_run_until_default / rf_race_wait. */
    // number of worker threads that drive this scheduler (M:N step 2b). The pool sets it to the host
    // core count; a directly-driven rf_sched (rf_sched_run, native tests) leaves the default 1.
    int workers_total;
    // workers currently parked on `cond` with nothing runnable. A deadlock is only real when EVERY
    // worker is idle (workers_idle == workers_total): with N>1 one idle worker while another still
    // runs a coroutine is not stuck. Inc/dec around each idle cond-wait in rf_sched_step.
    int workers_idle;
    // Per-worker local run deques (M:N step 3), one per pool worker (indexed by g_worker_id). NULL for
    // a directly-driven rf_sched (rf_sched_run / native tests) — those keep using only the injector.
    // Allocated by rf_pool_spawn_workers once workers_total is fixed. Each has its OWN lock (never held
    // together with s->lock's protected fields under a second deque lock — see the lock-ordering note).
    rf_deque* deques;
    rf_mutex lock;         /* guards the injector + live + cross_wakers + waiters + worker counts */
    rf_cond cond;          /* IDLE WORKERS (and the legacy single-thread driver) wait here. Work-add
                            * SIGNALS ONE (not broadcast) — the targeted-wake fix for the N-worker
                            * thundering herd; stealing lets the woken worker grab work off any deque. */
    rf_cond block_cond;    /* TOP-LEVEL pool blockers (a main/OS thread in retrieve!/race! at top level)
                            * wait here and re-check their target's status. Broadcast on a completion /
                            * race signal. Kept separate from `cond` so a single worker-wake can never be
                            * consumed by a blocker that has no work to do (the old lost-wakeup hazard
                            * that forced broadcast). Blockers are few, so broadcasting them stays cheap. */
};

/*
 * Thread-safety model. Today the pool runs ONE worker (the OS thread calling rf_sched_run); the
 * timer list and coroutine stacks are touched only there, so they need no lock. The INJECTOR queue
 * and `live` are different: rf_sched_wake may push to the injector from a DIFFERENT thread (a worker
 * that just finished the work a coroutine is awaiting). So the injector + live are guarded by
 * s->lock, and the run loop blocks on s->cond (instead of a bare sleep) whenever it has nothing
 * ready — an external wake pushes to the injector and signals the cond, interrupting the wait. The
 * loop drops the lock around rf_coro_resume so user code (which may re-enter the scheduler) never
 * runs with the lock held. (Step 2 generalizes "the timer list is single-thread" once N>1 workers
 * share the pool; at N=1 it holds.)
 */

/* The scheduler driving the current OS thread (set for the duration of rf_sched_run). */
static _Thread_local rf_sched* g_sched = NULL;

/* This thread's pool-worker index, or -1 if it is not a pool worker (the main thread, a `threaded`
 * task thread, or a directly-driven rf_sched). It selects which local deque a worker drains, and which
 * deque an enqueue performed BY a worker (spawn / make_ready / a park requeue) pushes to. -1 ⇒ route to
 * the shared injector. Set once at the top of rf_pool_worker_main; never changes for a given thread. */
static _Thread_local int g_worker_id = -1;

/* Per-worker xorshift32 PRNG state for randomized steal-victim selection (step 3). Thread-local so
 * workers never contend on it; seeded non-zero from the worker index at startup. rand() is avoided —
 * it is process-global and not meant for this. */
static _Thread_local uint32_t g_steal_rng = 0;

static uint32_t rf_steal_rand(void)
{
    uint32_t x = g_steal_rng;
    if (x == 0) {
        x = (uint32_t)(g_worker_id + 1) * 2654435761u; // Knuth multiplicative; never leaves it 0
        if (x == 0) {
            x = 0x9e3779b9u;
        }
    }
    x ^= x << 13;
    x ^= x >> 17;
    x ^= x << 5;
    g_steal_rng = x;
    return x;
}

/* Monotonic nanosecond clock — never goes backwards, unaffected by wall-clock changes. */
static uint64_t rf_now_ns(void)
{
#ifdef _WIN32
    static LARGE_INTEGER freq;
    if (freq.QuadPart == 0) {
        QueryPerformanceFrequency(&freq);
    }
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return (uint64_t)((c.QuadPart * 1000000000ULL) / (uint64_t)freq.QuadPart);
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000000ULL + (uint64_t)ts.tv_nsec;
#endif
}

// Append to the injector FIFO and mark the coroutine QUEUED. Callers hold s->lock. The QUEUED state
// is what dedups a redundant wake (rf_sched_make_ready no-ops on an already-queued coroutine).
static void rf_sched_push_injector(rf_sched* s, rf_coro* c)
{
    c->sched_next = NULL;
    atomic_store(&c->sched_state, RF_SCHED_QUEUED);
    if (s->injector_tail != NULL) {
        s->injector_tail->sched_next = c;
    } else {
        s->injector_head = c;
    }
    s->injector_tail = c;
}

// Pop the head of the injector FIFO and mark it RUNNING (caller holds s->lock), or NULL if empty.
// Only a QUEUED coroutine is ever in the injector, so a pop is the sole QUEUED->RUNNING transition —
// exactly one worker takes ownership of running it.
static rf_coro* rf_sched_pop_injector(rf_sched* s)
{
    rf_coro* c = s->injector_head;
    if (c == NULL) {
        return NULL;
    }
    s->injector_head = c->sched_next;
    if (s->injector_head == NULL) {
        s->injector_tail = NULL;
    }
    c->sched_next = NULL;
    atomic_store(&c->sched_state, RF_SCHED_RUNNING);
    return c;
}

// Route a now-runnable coroutine to a ready queue and mark it QUEUED. Caller holds s->lock. The queue
// discipline (design §3, Tokio/Go GMP): a coroutine made ready BY a pool worker goes to that worker's
// own local deque bottom (LIFO — the freshest child is cache-hot and runs next); one made ready from
// off-pool (main thread, a `threaded` task, a directly-driven rf_sched with no deques) goes to the
// shared injector. The deque push takes the deque's own lock, nested under s->lock (the sanctioned
// order); the QUEUED store is atomic so the owner's later pop can transition it off s->lock. Every
// enqueue-of-a-ready-coroutine funnels through here (spawn, make_ready's IDLE case, a park requeue).
static void rf_sched_enqueue_ready(rf_sched* s, rf_coro* c)
{
    if (g_worker_id >= 0 && s->deques != NULL) {
        atomic_store(&c->sched_state, RF_SCHED_QUEUED);
        rf_deque_push_bottom(&s->deques[g_worker_id], c);
    } else {
        rf_sched_push_injector(s, c);
    }
}

/* ---- Timer min-heap (M:N step 5) ------------------------------------------------------------- */
/* An array-backed binary min-heap of coroutines keyed by wake_ns (earliest at index 0), replacing the
 * O(n)-insert sorted list. Insert / pop-min are O(log n); an early wake removes an arbitrary coroutine
 * in O(log n) because each coroutine caches its heap slot in c->timer_idx. All ops hold s->lock. */

static void rf_timer_swap(rf_sched* s, int i, int j)
{
    rf_coro* a = s->timer_heap[i];
    rf_coro* b = s->timer_heap[j];
    s->timer_heap[i] = b; b->timer_idx = i;
    s->timer_heap[j] = a; a->timer_idx = j;
}

static void rf_timer_sift_up(rf_sched* s, int i)
{
    while (i > 0) {
        int parent = (i - 1) / 2;
        if (s->timer_heap[parent]->wake_ns <= s->timer_heap[i]->wake_ns) {
            break;
        }
        rf_timer_swap(s, i, parent);
        i = parent;
    }
}

static void rf_timer_sift_down(rf_sched* s, int i)
{
    int n = s->timer_count;
    for (;;) {
        int l = 2 * i + 1, r = 2 * i + 2, m = i;
        if (l < n && s->timer_heap[l]->wake_ns < s->timer_heap[m]->wake_ns) {
            m = l;
        }
        if (r < n && s->timer_heap[r]->wake_ns < s->timer_heap[m]->wake_ns) {
            m = r;
        }
        if (m == i) {
            break;
        }
        rf_timer_swap(s, i, m);
        i = m;
    }
}

/* Insert `c` onto the timer heap by its wake_ns. Caller holds s->lock — the heap is touched from a
 * worker parking a coroutine (park intent TIMER) AND, for deadline parks that are also externally
 * wakeable, unlinked by rf_sched_wake from another thread (also under s->lock). */
static void rf_sched_insert_timer(rf_sched* s, rf_coro* c)
{
    if (s->timer_count == s->timer_cap) {
        int ncap = (s->timer_cap == 0) ? 16 : s->timer_cap * 2;
        rf_coro** nh = (rf_coro**)realloc(s->timer_heap, (size_t)ncap * sizeof(rf_coro*));
        if (nh == NULL) {
            __rf_throw("OutOfMemoryError", "Failed to grow scheduler timer heap");
            return; /* unreachable */
        }
        s->timer_heap = nh;
        s->timer_cap = ncap;
    }
    int i = s->timer_count++;
    s->timer_heap[i] = c;
    c->timer_idx = i;
    rf_timer_sift_up(s, i);
}

/* Remove and return the earliest-deadline coroutine (heap min), or NULL if the heap is empty. */
static rf_coro* rf_timer_pop_min(rf_sched* s)
{
    if (s->timer_count == 0) {
        return NULL;
    }
    rf_coro* top = s->timer_heap[0];
    top->timer_idx = -1;
    int last = --s->timer_count;
    if (last > 0) {
        rf_coro* moved = s->timer_heap[last];
        s->timer_heap[0] = moved; moved->timer_idx = 0;
        s->timer_heap[last] = NULL;
        rf_timer_sift_down(s, 0);
    } else {
        s->timer_heap[0] = NULL;
    }
    return top;
}

/* Remove `c` from the timer heap if present (caller holds s->lock). Used when a deadline-parked
 * coroutine is woken by something OTHER than its timer (a worker completing the awaited task), so it
 * does not linger and get moved to ready a second time when its deadline fires. O(log n) via timer_idx.
 * The vacated slot is filled by the last element, which may need to move either up or down. */
static void rf_sched_unlink_timer(rf_sched* s, rf_coro* c)
{
    int i = c->timer_idx;
    if (i < 0 || i >= s->timer_count || s->timer_heap[i] != c) {
        return; // not on the heap
    }
    c->timer_idx = -1;
    int last = --s->timer_count;
    if (i == last) {
        s->timer_heap[last] = NULL;
        return;
    }
    rf_coro* moved = s->timer_heap[last];
    s->timer_heap[i] = moved; moved->timer_idx = i;
    s->timer_heap[last] = NULL;
    rf_timer_sift_up(s, i);
    if (moved->timer_idx == i) { // sift_up did not move it → it may belong deeper
        rf_timer_sift_down(s, i);
    }
}

// Make a coroutine runnable, driving the worker-safe state machine (M:N step 2b). Caller holds
// s->lock. This is the ONE choke point every wake path funnels through — rf_sched_wake, the awaiter
// wake at completion, and a race! competitor wake — so the RUNNING->NOTIFIED rule that prevents a
// second worker from re-queuing (and then double-resuming) a coroutine that is still executing lives
// in exactly one place:
//   IDLE     -> unlink from the timer list (no-op if not on it), enqueue ready (QUEUED)
//   RUNNING  -> NOTIFIED: do NOT enqueue; the worker running it re-queues it when it parks
//   QUEUED   -> already runnable (dedup — the old in_ready check)
//   NOTIFIED -> already flagged
//   DONE     -> completed; a stray wake is dropped
//
// Step 3: sched_state is atomic. make_ready runs under s->lock, but the ONLY transition it competes
// with off-lock is a worker's QUEUED->RUNNING pop/steal (under a deque lock) — and make_ready no-ops on
// QUEUED, so it never WRITES a coroutine a pop is concurrently claiming; the atomic load/store just
// keeps that read race-free. IDLE and RUNNING coroutines are not in any deque, so their transitions
// here are serialized against the running worker's post-resume bookkeeping by s->lock (no CAS needed).
static void rf_sched_make_ready(rf_sched* s, rf_coro* c)
{
    if (s == NULL || c == NULL) {
        return;
    }
    switch (atomic_load(&c->sched_state)) {
        case RF_SCHED_IDLE:
            rf_sched_unlink_timer(s, c);   // if it was deadline-parked, take it off the timer list
            rf_sched_enqueue_ready(s, c);  // -> QUEUED, to the waker's local deque or the injector
            break;
        case RF_SCHED_RUNNING:
            atomic_store(&c->sched_state, RF_SCHED_NOTIFIED);
            break;
        case RF_SCHED_QUEUED:
        case RF_SCHED_NOTIFIED:
        case RF_SCHED_DONE:
            break; // already runnable, already flagged, or gone — nothing to do
    }
}

rf_sched* rf_sched_create(void)
{
    rf_sched* s = (rf_sched*)calloc(1, sizeof(rf_sched));
    if (s == NULL) {
        __rf_throw("OutOfMemoryError", "Failed to allocate coroutine scheduler");
        return NULL; /* unreachable */
    }
    rf_mutex_init(&s->lock);
    rf_cond_init(&s->cond);
    rf_cond_init(&s->block_cond);
    s->workers_total = 1; /* one driver by default (native tests, rf_sched_run); the pool raises it */
    return s;
}

void rf_sched_destroy(rf_sched* s)
{
    if (s == NULL) {
        return;
    }
    rf_cond_destroy(&s->cond);
    rf_cond_destroy(&s->block_cond);
    rf_mutex_destroy(&s->lock);
    free(s->timer_heap);
    free(s);
}

/* Queue a (newly created, NEW) coroutine to run. It starts on its first resume by the loop.
 * Locks because a coroutine spawned from inside another coroutine touches the shared ready queue
 * while a worker thread may concurrently be waking someone. A coroutine spawned from INSIDE another
 * (the common case: this runs on a pool worker) lands on that worker's local deque — the LIFO
 * cache-hot child discipline (§3); one spawned from off-pool lands on the injector. */
void rf_sched_spawn(rf_sched* s, rf_coro* c)
{
    if (s == NULL || c == NULL) {
        return;
    }
    rf_mutex_lock(&s->lock);
    rf_sched_enqueue_ready(s, c);
    s->live++;
    /* Signal ONE idle worker (not broadcast): this is one unit of work, so one worker suffices — a
     * burst of K spawns emits K signals and wakes up to K workers, giving parallelism without the
     * N-worker thundering herd. The woken worker re-checks every source (its deque, the injector, a
     * steal) under s->lock before sleeping, so a signal that races its park is never lost; and if the
     * work sits on a different worker's deque, stealing lets the woken worker take it. Top-level
     * blockers wait on block_cond, so they cannot swallow this worker-wake. */
    rf_cond_signal(&s->cond);
    rf_mutex_unlock(&s->lock);
}

// Park the coroutine currently running under the loop until `delay_ns` from now, then resume it.
// Called from inside a coroutine body (e.g. by waitfor). No-op outside a running scheduler.
//
// We only RECORD the intent (+ deadline) and switch out; the worker inserts us into the timer list
// post-resume, under s->lock — or, if a wake turned us NOTIFIED meanwhile, re-queues us instead.
// Doing the timer insert on the worker rather than here is what keeps the park race-free at N>1:
// there is no window where we sit on the timer list yet are also being enqueued by a racing wake.
void rf_sched_park_timer(uint64_t delay_ns)
{
    rf_coro* self = rf_coro_current();
    if (g_sched == NULL || self == NULL) {
        return;
    }
    self->wake_ns = rf_now_ns() + delay_ns;
    self->park_intent = RF_PARK_TIMER;
    rf_coro_switch_out(); // the worker inserts us on the timer list and resumes us when it fires
}

// Park the current coroutine until `delay_ns` from now — BUT, unlike rf_sched_park_timer, it also
// stays externally wakeable (rf_sched_wake). The worker resumes it whichever happens first: the timer
// fires, or a wake unlinks it from the timer list (rf_sched_make_ready's IDLE case). Same RF_PARK_TIMER
// intent — the external wakeability is automatic, since a wake on an IDLE coroutine unlinks any timer.
// The substrate for a timed await; the caller (rf_task_await_coro_deadline) re-checks both conditions.
void rf_sched_park_deadline(uint64_t delay_ns)
{
    rf_coro* self = rf_coro_current();
    if (g_sched == NULL || self == NULL) {
        return;
    }
    self->wake_ns = rf_now_ns() + delay_ns;
    self->park_intent = RF_PARK_TIMER;
    rf_coro_switch_out();
}

// Park the current coroutine with NO wake condition the scheduler itself can satisfy: it is neither
// ready nor on a timer. Only an explicit rf_sched_wake (from any thread) re-queues it. This is how a
// coroutine awaits work running on another OS thread (a `threaded` Task) without blocking the worker —
// siblings keep running while it waits. The caller arms the waker (handing the worker this scheduler +
// its own coro handle) BEFORE parking; a wake that arrives first is not lost — it lands as NOTIFIED
// while we are still RUNNING, and the worker re-queues us immediately when we switch out.
void rf_sched_park_external(void)
{
    rf_coro* self = rf_coro_current();
    if (g_sched == NULL || self == NULL) {
        return;
    }
    self->park_intent = RF_PARK_EXTERNAL;
    rf_coro_switch_out(); // the worker will not resume us until someone calls rf_sched_wake
}

// Cooperative yield (the public suspend primitive): let other ready coroutines run, then continue.
// Under a scheduler we record a YIELD intent and switch out; the worker re-queues us behind the other
// ready coroutines — UNLIKE rf_sched_park_*, which wait for an external condition. Outside a scheduler
// it is a bare switch back to the resumer (a naive pump re-resumes). The may-suspend analysis seeds on
// this primitive.
void rf_coro_yield(void)
{
#ifdef RF_HAVE_CORO
    rf_coro* self = g_current_coro;
    if (self == NULL) {
        return; // not inside a coroutine
    }
    if (g_sched != NULL) {
        self->park_intent = RF_PARK_YIELD;
    }
    rf_coro_switch_out();
#endif
}

/* Make a parked coroutine runnable again. Safe to call from ANY thread — this is the bridge that
 * lets a worker thread hand a result back to a coroutine awaiting it on the scheduler thread.
 * The state machine (rf_sched_make_ready) drops a redundant wake and, crucially at N>1, records a
 * wake to a still-RUNNING coroutine as NOTIFIED instead of enqueuing it a second time. */
void rf_sched_wake(rf_sched* s, rf_coro* c)
{
    if (s == NULL || c == NULL) {
        return;
    }
    rf_mutex_lock(&s->lock);
    rf_sched_state before = atomic_load(&c->sched_state);
    // make_ready unlinks any deadline timer (IDLE case) and pushes to the injector, or flags NOTIFIED
    // if c is still running on a worker.
    rf_sched_make_ready(s, c);
    // Only signal when this wake actually enqueued c (IDLE->QUEUED). A no-op wake (already QUEUED, or
    // NOTIFIED — the running worker will requeue) has no new work for a sleeping worker to grab. Signal
    // ONE worker (not broadcast): one coroutine became runnable, so one worker suffices; it re-checks
    // all sources (incl. a steal) before sleeping, so a raced signal is never lost. Top-level blockers
    // wait on block_cond, not here, so this worker-wake cannot be swallowed by one. See rf_sched_spawn.
    if (before == RF_SCHED_IDLE) {
        rf_cond_signal(&s->cond);
    }
    rf_mutex_unlock(&s->lock);
}

/* Arm one outstanding cross-thread wake promise on `s` (see razorforge_runtime.h). A coroutine calls
 * this just before parking to await a wake that will arrive from ANOTHER thread, so the run loop does
 * not mistake the wait for a deadlock. Safe from any thread. */
void rf_sched_arm_cross_waker(rf_sched* s)
{
    if (s == NULL) {
        return;
    }
    rf_mutex_lock(&s->lock);
    s->cross_wakers++;
    rf_mutex_unlock(&s->lock);
}

/* Disarm one cross-thread wake promise on `s` (the wake resolved, or the wait was abandoned). Clamped
 * at zero so a stray unpaired disarm can never underflow into a phantom "wake still pending" (which
 * would only ever suppress a real deadlock report — never cause a spurious one). Safe from any thread. */
void rf_sched_disarm_cross_waker(rf_sched* s)
{
    if (s == NULL) {
        return;
    }
    rf_mutex_lock(&s->lock);
    if (s->cross_wakers > 0) {
        s->cross_wakers--;
    }
    rf_mutex_unlock(&s->lock);
}

// Try to steal one runnable coroutine from another worker's local deque (step 3). Random victim (a
// thread-local xorshift PRNG), self excluded. A stolen coroutine transitions QUEUED->RUNNING (atomic)
// exactly as an injector pop does — the victim deque's OWN lock serializes this against the victim's
// bottom-pop, so the same coroutine is never handed to two workers. Takes NO s->lock (only the victim's
// deque lock). Returns the stolen coroutine (now RUNNING) or NULL if every other deque was empty.
static rf_coro* rf_sched_try_steal(rf_sched* s)
{
    if (s->deques == NULL || s->workers_total <= 1) {
        return NULL;
    }
    int n = s->workers_total;
    uint32_t r = rf_steal_rand();
    for (int i = 0; i < n; i++) {
        int v = (int)((r + (uint32_t)i) % (uint32_t)n);
        if (v == g_worker_id) {
            continue; // never steal from ourselves
        }
        rf_coro* c = rf_deque_steal_top(&s->deques[v]);
        if (c != NULL) {
            atomic_store(&c->sched_state, RF_SCHED_RUNNING); // QUEUED -> RUNNING; we own it now
            return c;
        }
    }
    return NULL;
}

// Resume a coroutine this worker has claimed (state already RUNNING) and apply the outcome. Called with
// NO lock held: user code runs lock-free (it may re-enter the scheduler — spawn, park, wake) and only
// the post-resume bookkeeping takes s->lock. The record-then-apply split (park primitives merely set
// park_intent; the worker applies it here under s->lock) is what keeps a wake racing the park safe at
// N>1 — a wake that arrived during the run left the state NOTIFIED, and that wins over the park intent.
// A NOTIFIED requeue / a YIELD go through rf_sched_enqueue_ready, so they land on THIS worker's local
// deque (the coroutine just ran here — cache-hot).
static void rf_sched_run_coro(rf_sched* s, rf_coro* c)
{
    rf_coro_status st = rf_coro_resume(c);
    rf_mutex_lock(&s->lock);

    if (st == RF_CORO_COMPLETED) {
        atomic_store(&c->sched_state, RF_SCHED_DONE); // never runnable again; a stray wake is dropped
        if (!c->counted_done) {
            c->counted_done = 1; // count each completion exactly once, even if a stray wake
            s->live--;           //   re-queued it after it finished (the owner frees it later)
        }
        // Wake whoever is awaiting this coroutine's result. A coroutine parked in retrieve!
        // (rf_sched_run_until_default's in-coroutine branch) or a coroutine racing this one is
        // re-queued via make_ready (or flagged NOTIFIED if it is itself mid-run on another worker) —
        // a signal on `cond` picks it up (also wakes the legacy single-thread driver). A plain thread
        // blocked at top level waits on block_cond and re-checks its target's status, so broadcast that.
        if (c->awaiter != NULL) {
            rf_coro* w = c->awaiter;
            c->awaiter = NULL;
            rf_sched_make_ready(s, w);
        }
        rf_cond_signal(&s->cond);          // an enqueued awaiter coroutine / the legacy driver
        rf_cond_broadcast(&s->block_cond); // top-level blockers re-check their target's completion
        rf_mutex_unlock(&s->lock);
        return;
    }

    // PARKED. Apply the intent the coroutine recorded before switching out — UNLESS a wake arrived
    // while it ran (state is now NOTIFIED), in which case the wake wins: re-queue it and ignore the
    // park intent. The coroutine never touches the injector/timer/deque itself, so a wake racing its
    // park is always resolved here, under the lock, with full knowledge of whether it parked or was
    // notified.
    if (atomic_load(&c->sched_state) == RF_SCHED_NOTIFIED) {
        rf_sched_enqueue_ready(s, c); // -> QUEUED (this worker's local deque), run again promptly
    } else {
        switch (c->park_intent) {
            case RF_PARK_TIMER:
                rf_sched_insert_timer(s, c); // sorted by wake_ns; c stays IDLE until it fires/wakes
                atomic_store(&c->sched_state, RF_SCHED_IDLE);
                break;
            case RF_PARK_YIELD:
                rf_sched_enqueue_ready(s, c); // -> QUEUED, behind the other ready coroutines
                break;
            case RF_PARK_EXTERNAL:
            case RF_PARK_NONE:
            default:
                atomic_store(&c->sched_state, RF_SCHED_IDLE); // in no list; only a wake re-queues it
                break;
        }
    }
    c->park_intent = RF_PARK_NONE;
    rf_mutex_unlock(&s->lock);
}

// Claim one runnable coroutine (transitioning it QUEUED->RUNNING) or NULL. The step-3 pull order
// (design §3): this worker's own local deque bottom (LIFO, cache-hot) -> the shared injector -> steal a
// random victim's tail (FIFO). Only the injector pop touches s->lock (briefly); the deque pop and the
// steal use per-deque locks, so busy workers no longer serialize on the one global lock — the point of
// work-stealing. NULL for a non-worker driver (g_worker_id < 0, e.g. rf_sched_run) falls straight to
// the injector, so the legacy single-thread native-test path is unchanged.
static rf_coro* rf_sched_claim_work(rf_sched* s)
{
    if (g_worker_id >= 0 && s->deques != NULL) {
        rf_coro* c = rf_deque_pop_bottom(&s->deques[g_worker_id]);
        if (c != NULL) {
            atomic_store(&c->sched_state, RF_SCHED_RUNNING); // QUEUED -> RUNNING (deque lock dropped)
            return c;
        }
    }
    rf_mutex_lock(&s->lock);
    rf_coro* c = rf_sched_pop_injector(s); // QUEUED -> RUNNING under s->lock
    rf_mutex_unlock(&s->lock);
    if (c != NULL) {
        return c;
    }
    return rf_sched_try_steal(s);
}

/* Resume one ready coroutine if any (returns 1), else block until something can make progress and
 * return 0. The caller must NOT hold s->lock. Two phases (M:N step 3):
 *   FAST PATH — rf_sched_claim_work pulls from the local deque / injector / a steal WITHOUT holding
 *     s->lock across the whole check, so busy workers do not serialize on it.
 *   SLOW PATH — nothing found: take s->lock and RE-CHECK every source before sleeping. Every enqueue of
 *     a ready coroutine (spawn, make_ready, a park requeue) pushes AND signals the cond under s->lock,
 *     so once we hold it and still see nothing runnable, any later enqueue is guaranteed to signal the
 *     cond we are about to wait on — no wake is lost. The lock is dropped only around rf_coro_resume.
 * The shared body of the pool worker, rf_sched_run, and rf_sched_run_until. */
static int rf_sched_step(rf_sched* s)
{
    rf_coro* c = rf_sched_claim_work(s);
    if (c != NULL) {
        rf_sched_run_coro(s, c);
        return 1;
    }

    // SLOW PATH: re-check under s->lock so a wake/steal that raced the fast path is not lost.
    rf_mutex_lock(&s->lock);
    if (g_worker_id >= 0 && s->deques != NULL) {
        c = rf_deque_pop_bottom(&s->deques[g_worker_id]);
        if (c != NULL) {
            atomic_store(&c->sched_state, RF_SCHED_RUNNING);
        }
    }
    if (c == NULL) {
        c = rf_sched_pop_injector(s);
    }
    if (c == NULL) {
        c = rf_sched_try_steal(s); // s->lock held -> victim deque lock: the sanctioned nesting order
    }
    if (c != NULL) {
        rf_mutex_unlock(&s->lock);
        rf_sched_run_coro(s, c);
        return 1;
    }

    // Nothing ready. Wait for the earliest timer if any, otherwise purely for an external wake.
    if (s->timer_count > 0) {
        uint64_t now = rf_now_ns();
        uint64_t deadline = s->timer_heap[0]->wake_ns; // heap min = nearest deadline
        if (deadline > now) {
            // Idle on a timer: a pending timer is progress waiting to happen, so this is never a
            // deadlock — just wait out the nearest deadline (a wake can still interrupt us earlier).
            s->workers_idle++;
            rf_cond_wait_ns(&s->cond, &s->lock, deadline - now);
            s->workers_idle--;
        }
        now = rf_now_ns();
        while (s->timer_count > 0 && s->timer_heap[0]->wake_ns <= now) {
            rf_coro* t = rf_timer_pop_min(s);
            rf_sched_push_injector(s, t); // IDLE -> QUEUED
        }
    } else {
        // Nothing ready and no timer: every live coroutine is parked externally. The only thing that
        // can make progress is a cross-thread wake — and if none is outstanding, none can ever arrive:
        // every live coroutine is blocked on a send/receive/signal whose counterpart is itself a
        // parked coroutine on THIS pool. That is a genuine deadlock; diagnose it (locked decision §0.4)
        // instead of blocking forever. A channel park deliberately does NOT arm a cross-waker (RF-S632
        // keeps its counterpart on this same pool), so an all-coroutine channel deadlock lands here.
        //
        // Two guards keep this from firing spuriously:
        //  - waiters > 0 (step 2a): the pool worker runs continuously, so this branch is also reached
        //    when the program is merely idle between bursts with a background coroutine parked and
        //    nobody awaiting it (pending teardown). Not a deadlock.
        //  - workers_idle + 1 == workers_total (step 2b): with N>1 workers, one worker reaching here
        //    while another is still RUNNING a coroutine is not stuck — that coroutine may yet complete
        //    or wake someone. Only when THIS worker going idle makes ALL workers idle can nothing else
        //    make progress. (We have not incremented workers_idle yet, so compare against +1.)
        if (s->live > 0 && s->cross_wakers == 0 && s->waiters > 0 &&
            s->workers_idle + 1 == s->workers_total) {
            rf_mutex_unlock(&s->lock);
            __rf_throw("DeadlockError",
                       "all coroutines are parked and none is runnable — no send, receive, or wake "
                       "can ever make progress (deadlock)");
            return 0; // unreachable: __rf_throw exits
        }
        // A cross-thread wake is outstanding (threaded await / async I/O / signal / race), or other
        // workers are still busy: block until work arrives or a worker signals.
        s->workers_idle++;
        rf_cond_wait_forever(&s->cond, &s->lock);
        s->workers_idle--;
    }
    rf_mutex_unlock(&s->lock);
    return 0;
}

/* Drive all spawned coroutines to completion on this thread. Blocks on s->cond (not a bare sleep)
 * whenever nothing is ready, so a cross-thread rf_sched_wake interrupts the wait promptly. */
void rf_sched_run(rf_sched* s)
{
    if (s == NULL) {
        return;
    }
    rf_sched* prev = g_sched;
    g_sched = s;

    // rf_sched_step is self-locking now (step 3 prep): read the loop condition under a brief lock, then
    // let step block internally when nothing is ready. When the last coroutine completes inside a step,
    // live drops to 0 and the next check exits — same as the old lock-held-across-the-loop form.
    for (;;) {
        rf_mutex_lock(&s->lock);
        int done = (s->live <= 0);
        rf_mutex_unlock(&s->lock);
        if (done) {
            break;
        }
        rf_sched_step(s);
    }

    g_sched = prev;
}

/* Drive the scheduler only until `target` finishes (completes or is cancelled), then return —
 * leaving any other spawned coroutines parked for a later run/run_until. This is the entry point
 * for Coroutine[T].retrieve!(): `target` was spawned (at the suspended-routine call) onto this
 * thread's scheduler; retrieve! drives the loop until just that handle is done, so siblings spawned
 * earlier progress concurrently meanwhile but are not forced to completion. Reentrant: a coroutine
 * may itself call run_until on another handle (a nested loop on the same scheduler). */
void rf_sched_run_until(rf_sched* s, rf_coro* target)
{
    if (s == NULL || target == NULL) {
        return;
    }
    rf_sched* prev = g_sched;
    g_sched = s;

    /* `target` counts toward `live` until it finishes, so while it is unfinished there is always
     * something to wait on; rf_sched_step never busy-spins. step is self-locking now (step 3 prep);
     * target->status is flipped by rf_coro_resume on THIS thread inside step, so reading it here
     * between steps needs no lock. */
    while (target->status != RF_CORO_COMPLETED && target->status != RF_CORO_CANCELLED) {
        rf_sched_step(s);
    }

    g_sched = prev;
}

/* Wake a scheduler's run loop without targeting a specific coroutine. Used by a worker thread
 * completing a task that a top-level `race!` loop is waiting on — the loop holds no awaiter coroutine,
 * it re-polls all competitors under s->lock when woken. Safe to call from ANY thread. */
void rf_sched_signal(rf_sched* s)
{
    if (s == NULL) {
        return;
    }
    rf_mutex_lock(&s->lock);
    /* A top-level race! blocker waits on block_cond (broadcast it — blockers re-poll their whole set);
     * the legacy single-thread driver waits on `cond` (signal it). A coroutine racer is woken instead
     * through rf_sched_wake (its awaiter), not here. */
    rf_cond_broadcast(&s->block_cond);
    rf_cond_signal(&s->cond);
    rf_mutex_unlock(&s->lock);
}

/* ---- race!: drive a heterogeneous competitor set until the FIRST one completes ---------------- */
/*
 * A `race!` competitor set built incrementally by the stdlib `race!` routine (one entry per Agent,
 * in List order) and then driven on this thread's implicit scheduler until any entry completes.
 * Coroutine competitors are progressed by stepping the scheduler; thread competitors complete on
 * their own OS thread and signal the loop via rf_sched_signal (registered through race_sched). The
 * completion poll and the cond wait share s->lock, so a completion can never be lost between them.
 */
typedef struct rf_race {
    void** handles;   /* rf_coro* (kind 0) or rf_task* (kind 1)                   */
    uint8_t* kinds;   /* 0 = coroutine competitor, 1 = thread competitor          */
    intptr_t count;
    intptr_t cap;
} rf_race;

rf_race* rf_race_begin(void)
{
    rf_race* r = (rf_race*)calloc(1, sizeof(rf_race));
    if (r == NULL) {
        __rf_throw("OutOfMemoryError", "Failed to allocate race competitor set");
        return NULL; /* unreachable */
    }
    return r;
}

static void rf_race_push(rf_race* r, void* handle, uint8_t kind)
{
    if (r == NULL) {
        return;
    }
    if (r->count == r->cap) {
        intptr_t ncap = (r->cap == 0) ? 4 : r->cap * 2;
        void** nh = (void**)realloc(r->handles, (size_t)ncap * sizeof(void*));
        uint8_t* nk = (uint8_t*)realloc(r->kinds, (size_t)ncap * sizeof(uint8_t));
        if (nh == NULL || nk == NULL) {
            free(nh); free(nk);
            __rf_throw("OutOfMemoryError", "Failed to grow race competitor set");
            return; /* unreachable */
        }
        r->handles = nh;
        r->kinds = nk;
        r->cap = ncap;
    }
    r->handles[r->count] = handle;
    r->kinds[r->count] = kind;
    r->count++;
}

void rf_race_add_coro(rf_race* r, rf_coro* c) { rf_race_push(r, (void*)c, 0); }
void rf_race_add_task(rf_race* r, rf_task* t) { rf_race_push(r, (void*)t, 1); }

// Drive the set until one competitor completes; return its index in add (List) order, or -1 if the
// set is empty. The pool worker(s) drive the coroutine competitors; this racer only WAITS to be woken
// when the first one finishes. How it waits depends on WHERE it runs (step 2b — both paths park/block,
// never nested-step, so it is correct at N>1):
//   - Top-level thread (self == NULL): BLOCK on the pool cond and re-poll. A coroutine competitor's
//     completion broadcasts the cond (rf_sched_step); a thread competitor's completion signals it via
//     rf_sched_signal (race_sched, race_waiter == NULL).
//   - Inside a coroutine (self != NULL): PARK external and re-poll on each wake. We register `self` as
//     the awaiter of every coroutine competitor (its completion re-queues us) and as the race_waiter of
//     every thread competitor (its completion calls rf_sched_wake(s, self)). Duplicate/spurious wakes
//     are harmless — we re-poll the whole set under the lock.
// Either way a cross-waker is armed per thread competitor so the worker's deadlock detector stays quiet
// while those run on their own OS threads.
intptr_t rf_race_wait(rf_race* r)
{
    if (r == NULL || r->count == 0) {
        return -1;
    }
    rf_sched* s = rf_sched_thread_default();
    rf_coro* self = rf_coro_current();

    for (intptr_t i = 0; i < r->count; i++) {
        if (r->kinds[i] == 1) {
            rf_task_race_register((rf_task*)r->handles[i], s, self);
            rf_sched_arm_cross_waker(s);
        }
    }

    intptr_t winner = -1;
    rf_mutex_lock(&s->lock);
    s->waiters++; // blocked awaiting the first competitor — arms the worker's deadlock detector

    // A coroutine racer parks; point every coroutine competitor's awaiter slot at us so its completion
    // re-queues us. Set under s->lock (rf_sched_step reads awaiter under the same lock).
    if (self != NULL) {
        for (intptr_t i = 0; i < r->count; i++) {
            if (r->kinds[i] == 0 && r->handles[i] != NULL) {
                ((rf_coro*)r->handles[i])->awaiter = self;
            }
        }
    }

    for (;;) {
        for (intptr_t i = 0; i < r->count; i++) {
            if (r->kinds[i] == 0) {
                rf_coro* c = (rf_coro*)r->handles[i];
                if (c != NULL && (c->status == RF_CORO_COMPLETED || c->status == RF_CORO_CANCELLED)) {
                    winner = i;
                    break;
                }
            } else {
                if (rf_task_status_get((rf_task*)r->handles[i]) == RF_TASK_COMPLETED) {
                    winner = i;
                    break;
                }
            }
        }
        if (winner >= 0) {
            break;
        }
        if (self != NULL) {
            // Park until a competitor wakes us. Drop the lock across the switch out; a completion
            // racing this park finds us RUNNING and flags NOTIFIED, so the worker re-queues us — no
            // wake is lost (same guarantee as rf_sched_run_until_default's in-coroutine branch).
            rf_mutex_unlock(&s->lock);
            rf_sched_park_external();
            rf_mutex_lock(&s->lock);
        } else {
            // Top-level racer: the workers drive the competitors; wait on block_cond (a competitor's
            // completion broadcasts it via rf_sched_run_coro / rf_sched_signal) and re-poll.
            rf_cond_wait_forever(&s->block_cond, &s->lock);
        }
    }

    // Clear our awaiter registration from any competitor that has not already cleared it at completion.
    if (self != NULL) {
        for (intptr_t i = 0; i < r->count; i++) {
            if (r->kinds[i] == 0 && r->handles[i] != NULL) {
                rf_coro* c = (rf_coro*)r->handles[i];
                if (c->awaiter == self) {
                    c->awaiter = NULL;
                }
            }
        }
    }
    s->waiters--;
    rf_mutex_unlock(&s->lock);

    for (intptr_t i = 0; i < r->count; i++) {
        if (r->kinds[i] == 1) {
            rf_task_race_register((rf_task*)r->handles[i], NULL, NULL);
            rf_sched_disarm_cross_waker(s); // balance the arm above
        }
    }

    return winner;
}

void rf_race_end(rf_race* r)
{
    if (r == NULL) {
        return;
    }
    free(r->handles);
    free(r->kinds);
    free(r);
}

/* True (1) when the caller is running inside a coroutine that is driven by a scheduler — i.e. a
 * park (rf_sched_park_timer) would actually suspend and let siblings run. False (0) on a plain
 * thread, or in a coroutine pumped without a scheduler (where a "park" could not be honored).
 * Lets `waitfor` be uncolored: park under a scheduler, OS-sleep otherwise. */
int rf_in_coroutine(void)
{
    return (g_sched != NULL && rf_coro_current() != NULL) ? 1 : 0;
}

/* The scheduler currently driving this OS thread (the one inside rf_sched_run / run_until), or
 * NULL if none. The task↔coro await bridge reads this to record which scheduler must wake the
 * coroutine when the awaited work completes. */
rf_sched* rf_sched_current(void)
{
    return g_sched;
}

// ---- Process-wide scheduler pool (§6) + background workers — M:N build step 2b ----------------
//
// (internal-wiki/v0.3.x-mn-scheduler.md §8.) Coroutines no longer run on the thread that spawned them.
// A single PROCESS-WIDE pool (g_pool) owns them and a fleet of background WORKER threads drains its
// injector — resuming coroutines, firing timers, parking when idle. A `suspended routine` call spawns
// onto the pool; retrieve! (rf_sched_run_until_default) either PARKS (when the caller is itself a
// coroutine on a worker — the workers keep driving siblings) or BLOCKS the calling thread on a
// completion signal (top level). This replaces the old per-thread implicit scheduler.
//
// Step 2b/3: N = host core count workers (was N=1 in 2a), each with a local deque + work-stealing. The
// worker-safe park/wake state machine (atomic rf_sched_state / rf_sched_make_ready) keeps a wake racing
// a park from double-resuming; the deadlock detector counts idle workers so it only fires when ALL are
// stuck. Workers are daemons (run for the process lifetime; the OS reaps them at exit), created lazily
// on first coroutine spawn/drive. N is fixed once the pool starts.
//
// Still deferred to later steps: a shared timer min-heap (step 5), targeted per-worker signalling in
// place of the broadcast-on-work-add (correct but a thundering herd), and a Chase–Lev lock-free deque.

// Number of pool workers = host logical core count (min 1), unless RF_WORKERS overrides it.
static int rf_host_core_count(void)
{
#ifdef _WIN32
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    int n = (int)si.dwNumberOfProcessors;
#else
    long n = sysconf(_SC_NPROCESSORS_ONLN);
#endif
    return (n >= 1) ? (int)n : 1;
}

// Worker count for the pool: RF_WORKERS if set to a positive integer (clamped to a sane ceiling), else
// the host core count. The env override is the determinism escape hatch (design §7.5/§9.A): RF_WORKERS=1
// gives a single worker — no stealing, one local deque drained in order — reducing scheduling to a
// reproducible baseline for tests, and letting a flaky interleaving be pinned down. An unset / empty /
// non-positive / unparseable value falls back to the core count. Queried once at pool startup.
#define RF_WORKERS_MAX 4096
static int rf_pool_worker_count(void)
{
    const char* env = getenv("RF_WORKERS");
    if (env != NULL && env[0] != '\0') {
        char* end = NULL;
        long v = strtol(env, &end, 10);
        if (end != env && v >= 1) {
            return (v > RF_WORKERS_MAX) ? RF_WORKERS_MAX : (int)v;
        }
    }
    return rf_host_core_count();
}

static rf_sched* g_pool = NULL;

/* Heap-passed startup argument for a pool worker: which scheduler it drives and its worker index.
 * Freed by the worker itself once it has copied both out — the daemon is never joined, so the
 * spawning thread cannot free it. */
typedef struct rf_worker_arg {
    rf_sched* s;
    int index;
} rf_worker_arg;

#ifdef _WIN32
static unsigned __stdcall rf_pool_worker_main(void* arg)
#else
static void* rf_pool_worker_main(void* arg)
#endif
{
    rf_worker_arg* wa = (rf_worker_arg*)arg;
    rf_sched* s = wa->s;
    g_worker_id = wa->index; /* identifies this worker's local deque (step 3) */
    free(wa);
    g_sched = s; /* so rf_sched_current()/park/wake on this worker resolve to the pool */
    for (;;) {
        /* Drive the pool forever: resume a ready coroutine, fire due timers, or park on the cond
         * when idle. rf_sched_step is self-locking (it acquires s->lock only for the queue op and the
         * idle wait, dropping it around the resume) — the worker holds NO lock between iterations, the
         * seam step 3's local-deque draining slots into. It throws DeadlockError only while live>0 with
         * nothing runnable and no cross-waker outstanding; when merely between bursts (live==0) it parks
         * on the cond and a later spawn/wake signals it. */
        rf_sched_step(s);
    }
    /* unreachable (daemon) */
#ifdef _WIN32
    return 0;
#else
    return NULL;
#endif
}

// Start the pool's worker threads as daemons (never joined; the OS reaps them at process exit). The
// worker count is recorded on the scheduler first so the deadlock detector's all-idle test is right
// from the moment the first worker runs.
static void rf_pool_spawn_workers(rf_sched* s)
{
    s->workers_total = rf_pool_worker_count();

    // Allocate one local run deque per worker BEFORE starting any worker, so a worker (or a thief)
    // never races deque creation. Indexed by g_worker_id. (Step 3 skeleton: allocated + scanned by
    // rf_sched_remove, but not yet drained/pushed — routing stays on the injector.)
    s->deques = (rf_deque*)calloc((size_t)s->workers_total, sizeof(rf_deque));
    if (s->deques == NULL) {
        __rf_throw("OutOfMemoryError", "Failed to allocate worker run deques");
        return; /* unreachable */
    }
    for (int i = 0; i < s->workers_total; i++) {
        rf_deque_init(&s->deques[i]);
    }

    for (int i = 0; i < s->workers_total; i++) {
        // Each worker gets its own heap arg (scheduler + index); the worker frees it. A daemon thread
        // is never joined, so this cannot live on the spawning thread's stack.
        rf_worker_arg* wa = (rf_worker_arg*)malloc(sizeof(rf_worker_arg));
        if (wa == NULL) {
            __rf_throw("OutOfMemoryError", "Failed to allocate pool worker startup argument");
            return; /* unreachable */
        }
        wa->s = s;
        wa->index = i;
#ifdef _WIN32
        uintptr_t h = _beginthreadex(NULL, 0, rf_pool_worker_main, wa, 0, NULL);
        if (h == 0) {
            __rf_throw("TaskSpawnError", "Failed to start scheduler pool worker thread");
        }
        CloseHandle((HANDLE)h);
#else
        pthread_t tid;
        if (pthread_create(&tid, NULL, rf_pool_worker_main, wa) != 0) {
            __rf_throw("TaskSpawnError", "Failed to start scheduler pool worker thread");
        }
        pthread_detach(tid);
#endif
    }
}

/* One-time pool bootstrap: create the shared scheduler and start its worker(s). Runs exactly once
 * even under concurrent first calls from several threads (e.g. two `threaded` routines each spawning
 * a coroutine), via the platform one-time-init primitive. */
#ifdef _WIN32
static INIT_ONCE g_pool_once = INIT_ONCE_STATIC_INIT;
static BOOL CALLBACK rf_pool_init_cb(PINIT_ONCE once, PVOID param, PVOID* ctx)
{
    (void)once; (void)param; (void)ctx;
    g_pool = rf_sched_create();
    rf_pool_spawn_workers(g_pool);
    return TRUE;
}
#else
static pthread_once_t g_pool_once = PTHREAD_ONCE_INIT;
static void rf_pool_init_cb(void)
{
    g_pool = rf_sched_create();
    rf_pool_spawn_workers(g_pool);
}
#endif

/* The process-wide scheduler pool, created (and its worker started) on first use. Named
 * `..._thread_default` for source compatibility with the callers that predate the pool; it now
 * returns the ONE shared pool regardless of calling thread. */
rf_sched* rf_sched_thread_default(void)
{
#ifdef _WIN32
    InitOnceExecuteOnce(&g_pool_once, rf_pool_init_cb, NULL, NULL);
#else
    pthread_once(&g_pool_once, rf_pool_init_cb);
#endif
    return g_pool;
}

/* Spawn a NEW coroutine onto the process pool. Emitted at a `suspended routine` call site right
 * after rf_coro_create, so the coroutine is queued (and a worker exists to run it) immediately. */
void rf_sched_spawn_default(rf_coro* c)
{
    rf_sched_spawn(rf_sched_thread_default(), c);
}

// Await `target` to completion — the retrieve! engine. The pool workers drive coroutines; the caller
// does NOT step the loop. Two paths:
//   - Inside a coroutine (running on a worker): PARK until target completes. Register as target's
//     awaiter and switch out so the workers keep driving siblings — including target; the worker that
//     completes target re-queues us. Re-check under the lock so no wake is lost.
//   - On a plain thread (top level): BLOCK on the pool cond until a worker drives target to completion.
// At N>1 a wake racing our park is resolved by the state machine: a completion firing while we are
// still RUNNING flags us NOTIFIED, and our worker re-queues us on switch-out rather than parking us —
// so target->awaiter never strands us. Setting awaiter each loop iteration tolerates a spurious wake.
void rf_sched_run_until_default(rf_coro* target)
{
    if (target == NULL) {
        return;
    }
    rf_sched* s = rf_sched_thread_default();
    rf_coro* self = rf_coro_current();
    rf_mutex_lock(&s->lock);
    s->waiters++; /* we are now a blocker awaiting `target` — arms the worker's deadlock detector */
    if (self != NULL) {
        while (target->status != RF_CORO_COMPLETED && target->status != RF_CORO_CANCELLED) {
            target->awaiter = self;
            rf_mutex_unlock(&s->lock);
            rf_sched_park_external(); /* switch out; the worker re-queues us when target completes */
            rf_mutex_lock(&s->lock);
        }
        target->awaiter = NULL; /* clear any registration left by a spurious wake */
    } else {
        // Top-level blocker: the workers drive `target`; wait on block_cond (its completion in
        // rf_sched_run_coro broadcasts block_cond) and re-check the status.
        while (target->status != RF_CORO_COMPLETED && target->status != RF_CORO_CANCELLED) {
            rf_cond_wait_forever(&s->block_cond, &s->lock);
        }
    }
    s->waiters--;
    rf_mutex_unlock(&s->lock);
}

/* Unlink `c` from a scheduler's ready queue or timer list and decrement `live`. Returns 1 if it was
 * found (and thus removed + uncounted), 0 otherwise. Used by Coroutine[T].$destroy to detach a
 * spawned-but-never-finished coroutine from the scheduler BEFORE rf_coro_abandon frees it — without
 * this the scheduler would hold a dangling pointer / an inflated live count. A coroutine that is
 * already completed is in neither list (already uncounted) → returns 0, no double-decrement. */
static int rf_sched_remove(rf_sched* s, rf_coro* c)
{
    if (s == NULL || c == NULL) {
        return 0;
    }
    int found = 0;
    rf_mutex_lock(&s->lock);

    /* injector FIFO */
    rf_coro** rlink = &s->injector_head;
    rf_coro* prev = NULL;
    while (*rlink != NULL) {
        if (*rlink == c) {
            *rlink = c->sched_next;
            if (s->injector_tail == c) {
                s->injector_tail = prev;
            }
            c->sched_next = NULL;
            // detached from the injector; about to be abandoned
            atomic_store(&c->sched_state, RF_SCHED_IDLE);
            found = 1;
            break;
        }
        prev = *rlink;
        rlink = &(*rlink)->sched_next;
    }

    /* timer heap (only if not found in ready — a coroutine is in at most one). O(log n) via timer_idx;
     * unlink no-ops if c is not actually on the heap, so guard `found` on the count dropping. */
    if (!found && c->timer_idx >= 0) {
        int before = s->timer_count;
        rf_sched_unlink_timer(s, c);
        if (s->timer_count < before) {
            found = 1;
        }
    }

    /* per-worker local deques (step 3): a QUEUED coroutine may sit in a worker's local deque instead
     * of the injector. Scan each (s->lock held → deque lock nested, the sanctioned lock order). Skipped
     * for a directly-driven rf_sched, which has none. A coroutine is in at most one place, so stop on
     * the first hit. The deque stores pointers (not via sched_next), so no sched_next fixup is needed. */
    if (!found && s->deques != NULL) {
        for (int i = 0; i < s->workers_total; i++) {
            if (rf_deque_remove(&s->deques[i], c)) {
                atomic_store(&c->sched_state, RF_SCHED_IDLE);
                found = 1;
                break;
            }
        }
    }

    if (found) {
        s->live--;
    }
    rf_mutex_unlock(&s->lock);
    return found;
}

/* Detach `c` from the process pool (if present). The $destroy-side convenience over rf_sched_remove.
 * Call BEFORE rf_coro_abandon on a coroutine that was spawned but never retrieved. Safe if the pool
 * was never created (g_pool == NULL) — rf_sched_remove treats a NULL scheduler as "not found". */
int rf_sched_unschedule_default(rf_coro* c)
{
    return rf_sched_remove(g_pool, c);
}

/* The monotonic nanosecond clock the scheduler uses for timers, exposed for the task↔coro deadline
 * bridge (so a timed await measures its deadline on the same clock the timer list is sorted by). */
uint64_t rf_monotonic_now_ns(void)
{
    return rf_now_ns();
}

// Number of worker threads driving the process pool (the fixed host-core-count, min 1). Creates the
// pool on first call so the count is always the real one. Exposed for introspection and for
// core-count-robust concurrency tests: migration can only occur when this is > 1.
uint64_t rf_sched_worker_count(void)
{
    rf_sched* s = rf_sched_thread_default();
    return (s != NULL) ? (uint64_t)s->workers_total : 1u;
}