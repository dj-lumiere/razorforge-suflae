/*
 * coro_runtime.c — v0.2.0 stackful coroutine primitive (Phase 1: context-switch spike).
 *
 * Thin, ABI-neutral wrapper over the vendored libco context switch. Exposes the four
 * substrate primitives the v0.2.0 design (internal-wiki/v0.2.0-coroutine-primitive.md §8)
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

#include <stdlib.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <time.h>
#include <errno.h>
#endif

#ifdef HAVE_LIBCO
#include "libco.h"
#endif

/* Minimal portable mutex + condition variable for the scheduler's cross-thread wake. Uses the
 * platform primitives directly (Win32 CONDITION_VARIABLE / POSIX pthread) so this file still
 * compiles standalone for the spikes. The Win32 timed wait takes a relative ms timeout; POSIX
 * needs an absolute time, computed from CLOCK_REALTIME — both wrapped by rf_cond_wait_ns. */
#ifdef _WIN32
typedef CRITICAL_SECTION rf_mutex;
typedef CONDITION_VARIABLE rf_cond;
#else
#include <pthread.h>
typedef pthread_mutex_t rf_mutex;
typedef pthread_cond_t rf_cond;
#endif

static void rf_mutex_init(rf_mutex* m)
{
#ifdef _WIN32
    InitializeCriticalSection(m);
#else
    pthread_mutex_init(m, NULL);
#endif
}
static void rf_mutex_destroy(rf_mutex* m)
{
#ifdef _WIN32
    DeleteCriticalSection(m);
#else
    pthread_mutex_destroy(m);
#endif
}
static void rf_mutex_lock(rf_mutex* m)
{
#ifdef _WIN32
    EnterCriticalSection(m);
#else
    pthread_mutex_lock(m);
#endif
}
static void rf_mutex_unlock(rf_mutex* m)
{
#ifdef _WIN32
    LeaveCriticalSection(m);
#else
    pthread_mutex_unlock(m);
#endif
}
static void rf_cond_init(rf_cond* c)
{
#ifdef _WIN32
    InitializeConditionVariable(c);
#else
    pthread_cond_init(c, NULL);
#endif
}
static void rf_cond_destroy(rf_cond* c)
{
#ifndef _WIN32
    pthread_cond_destroy(c);
#else
    (void)c;
#endif
}
static void rf_cond_signal(rf_cond* c)
{
#ifdef _WIN32
    WakeConditionVariable(c);
#else
    pthread_cond_signal(c);
#endif
}
/* Wait on the cond with the mutex held; returns after a signal or `timeout_ns` elapses. */
static void rf_cond_wait_ns(rf_cond* c, rf_mutex* m, uint64_t timeout_ns)
{
#ifdef _WIN32
    DWORD ms = (DWORD)((timeout_ns + 999999ULL) / 1000000ULL);
    SleepConditionVariableCS(c, m, ms);
#else
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    uint64_t total = (uint64_t)ts.tv_sec * 1000000000ULL + (uint64_t)ts.tv_nsec + timeout_ns;
    ts.tv_sec = (time_t)(total / 1000000000ULL);
    ts.tv_nsec = (long)(total % 1000000000ULL);
    pthread_cond_timedwait(c, m, &ts);
#endif
}
/* Wait on the cond with the mutex held until a signal (no timeout). */
static void rf_cond_wait_forever(rf_cond* c, rf_mutex* m)
{
#ifdef _WIN32
    SleepConditionVariableCS(c, m, INFINITE);
#else
    pthread_cond_wait(c, m);
#endif
}

/* Default coroutine stack, in bytes, when the caller passes stack_size == 0. libco reserves
 * this eagerly today; growable demand-paged stacks are sibling Phase-2 work (design §9.2). */
#define RF_CORO_DEFAULT_STACK (256u * 1024u)

struct rf_coro {
    rf_context_entry_fn entry;     /* user routine to run inside the coroutine            */
    void* userdata;                /* opaque argument handed to entry                     */
    rf_coro_status status;         /* NEW -> RUNNING -> {PARKED -> RUNNING}* -> COMPLETED */
    rf_cancel_frame* cf_top;       /* top of the cancellation shadow stack (NULL = empty) */
    struct rf_coro* sched_next;    /* scheduler link: ready queue OR timer list (one at a time) */
    uint64_t wake_ns;              /* monotonic deadline this coroutine is parked until         */
    int in_ready;                  /* 1 while queued in the ready FIFO (dedups external wakes)  */
#ifdef HAVE_LIBCO
    cothread_t thread;             /* libco context for this coroutine's own stack        */
    cothread_t resumer;            /* context to switch back to on yield/finish           */
#endif
};

#ifdef HAVE_LIBCO
/* The coroutine currently executing on THIS OS thread. NULL when running ordinary
 * (non-coroutine) code. Set by resume immediately before switching in so the trampoline and
 * yield can recover their own rf_coro* without libco passing an argument (co entry is
 * void(*)(void)). Thread-local: each OS thread drives its own coroutines independently. */
static _Thread_local rf_coro* g_current_coro = NULL;

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
#endif

rf_coro* rf_coro_create(rf_context_entry_fn entry, void* userdata, size_t stack_size)
{
    if (entry == NULL) {
        return NULL;
    }

    rf_coro* coro = (rf_coro*)calloc(1, sizeof(rf_coro));
    if (coro == NULL) {
        return NULL;
    }

    coro->entry = entry;
    coro->userdata = userdata;
    coro->status = RF_CORO_NEW;

#ifdef HAVE_LIBCO
    unsigned int bytes = (stack_size == 0) ? RF_CORO_DEFAULT_STACK : (unsigned int)stack_size;
    coro->thread = co_create(bytes, rf_coro_trampoline);
    if (coro->thread == NULL) {
        free(coro);
        return NULL;
    }
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

#ifdef HAVE_LIBCO
    coro->resumer = co_active();
    rf_coro* prev = g_current_coro;
    g_current_coro = coro;
    coro->status = RF_CORO_RUNNING;

    co_switch(coro->thread); /* runs trampoline (first time) or returns from yield */

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

void rf_coro_yield(void)
{
#ifdef HAVE_LIBCO
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

void rf_coro_delete(rf_coro* coro)
{
    if (coro == NULL) {
        return;
    }
#ifdef HAVE_LIBCO
    if (coro->thread != NULL) {
        co_delete(coro->thread);
    }
#endif
    free(coro);
}

/* ---- Cancellation shadow stack (Phase 3) -------------------------------------------------- */

void rf_coro_cf_push(rf_cancel_frame* frame, void* value_ptr, rf_destroy_fn destroy_fn)
{
    if (frame == NULL) {
        return;
    }
#ifdef HAVE_LIBCO
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
#ifdef HAVE_LIBCO
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

/* The coroutine running on this OS thread, or NULL outside any coroutine. Bridges the
 * libco-gated g_current_coro so the scheduler (compiled regardless of HAVE_LIBCO) can use it. */
static rf_coro* rf_coro_current(void)
{
#ifdef HAVE_LIBCO
    return g_current_coro;
#else
    return NULL;
#endif
}

/* ---- Single-thread cooperative scheduler (v0.2.0 async) ----------------------------------- */
/*
 * A run loop that drives many coroutines on ONE OS thread. A coroutine parks itself on a wake
 * condition (today: a monotonic timer, via rf_sched_park_timer) and yields back to the loop; the
 * loop resumes it when the condition is met. This is what makes `waitfor` inside a coroutine park
 * (cheap) instead of blocking the thread, and lets spawned coroutines progress concurrently.
 */

struct rf_sched {
    rf_coro* ready_head;   /* FIFO of coroutines ready to resume now            */
    rf_coro* ready_tail;
    rf_coro* timers;       /* coroutines parked on a deadline, sorted ascending */
    int live;              /* coroutines spawned but not yet completed          */
    rf_mutex lock;         /* guards ready_head/tail + live against worker threads */
    rf_cond cond;          /* run loop waits here; an external wake signals it      */
};

/*
 * Thread-safety model. The scheduler runs on ONE OS thread (the one calling rf_sched_run); the
 * timer list and coroutine stacks are touched only there, so they need no lock. The READY queue
 * and `live` are different: rf_sched_wake may push to ready from a DIFFERENT thread (a worker that
 * just finished the work a coroutine is awaiting). So ready + live are guarded by s->lock, and the
 * run loop blocks on s->cond (instead of a bare sleep) whenever it has nothing ready — an external
 * wake pushes to ready and signals the cond, interrupting the wait. The loop drops the lock around
 * rf_coro_resume so user code (which may re-enter the scheduler) never runs with the lock held.
 */

/* The scheduler driving the current OS thread (set for the duration of rf_sched_run). */
static _Thread_local rf_sched* g_sched = NULL;

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

/* Append to the ready FIFO. Callers hold s->lock. Marks the coroutine queued so a redundant
 * external wake (rf_sched_wake) can't enqueue it twice. */
static void rf_sched_push_ready(rf_sched* s, rf_coro* c)
{
    c->sched_next = NULL;
    c->in_ready = 1;
    if (s->ready_tail != NULL) {
        s->ready_tail->sched_next = c;
    } else {
        s->ready_head = c;
    }
    s->ready_tail = c;
}

/* Pop the head of the ready FIFO (caller holds s->lock), or NULL if empty. */
static rf_coro* rf_sched_pop_ready(rf_sched* s)
{
    rf_coro* c = s->ready_head;
    if (c == NULL) {
        return NULL;
    }
    s->ready_head = c->sched_next;
    if (s->ready_head == NULL) {
        s->ready_tail = NULL;
    }
    c->sched_next = NULL;
    c->in_ready = 0;
    return c;
}

/* Insert into the timer list keeping it sorted by wake_ns ascending (earliest first). */
static void rf_sched_insert_timer(rf_sched* s, rf_coro* c)
{
    rf_coro** link = &s->timers;
    while (*link != NULL && (*link)->wake_ns <= c->wake_ns) {
        link = &(*link)->sched_next;
    }
    c->sched_next = *link;
    *link = c;
}

rf_sched* rf_sched_create(void)
{
    rf_sched* s = (rf_sched*)calloc(1, sizeof(rf_sched));
    if (s == NULL) {
        return NULL;
    }
    rf_mutex_init(&s->lock);
    rf_cond_init(&s->cond);
    return s;
}

void rf_sched_destroy(rf_sched* s)
{
    if (s == NULL) {
        return;
    }
    rf_cond_destroy(&s->cond);
    rf_mutex_destroy(&s->lock);
    free(s);
}

/* Queue a (newly created, NEW) coroutine to run. It starts on its first resume by the loop.
 * Locks because a coroutine spawned from inside another coroutine touches the shared ready queue
 * while a worker thread may concurrently be waking someone. */
void rf_sched_spawn(rf_sched* s, rf_coro* c)
{
    if (s == NULL || c == NULL) {
        return;
    }
    rf_mutex_lock(&s->lock);
    rf_sched_push_ready(s, c);
    s->live++;
    rf_cond_signal(&s->cond); /* in case the loop is parked waiting for external work */
    rf_mutex_unlock(&s->lock);
}

/* Park the coroutine currently running under the loop until `delay_ns` from now, then resume it.
 * Called from inside a coroutine body (e.g. by waitfor). No-op outside a running scheduler. */
void rf_sched_park_timer(uint64_t delay_ns)
{
    rf_sched* s = g_sched;
    rf_coro* self = rf_coro_current();
    if (s == NULL || self == NULL) {
        return;
    }
    self->wake_ns = rf_now_ns() + delay_ns;
    rf_sched_insert_timer(s, self);
    rf_coro_yield(); /* switch back to the run loop; it resumes us when the timer fires */
}

/* Park the current coroutine with NO wake condition the scheduler itself can satisfy: it is
 * neither ready nor on a timer. Only an explicit rf_sched_wake (from any thread) re-queues it.
 * This is how a coroutine awaits work running on another OS thread (a `threaded` Task) without
 * blocking the scheduler thread — siblings keep running while it waits. The caller is responsible
 * for arming the waker (handing the worker this scheduler + its own coro handle) BEFORE parking;
 * a wake that arrives first is not lost (it lands in the ready queue, so the next yield-back finds
 * the coroutine ready immediately). */
void rf_sched_park_external(void)
{
    if (g_sched == NULL || rf_coro_current() == NULL) {
        return;
    }
    rf_coro_yield(); /* the run loop will not resume us until someone calls rf_sched_wake */
}

/* Make a parked coroutine runnable again. Safe to call from ANY thread — this is the bridge that
 * lets a worker thread hand a result back to a coroutine awaiting it on the scheduler thread.
 * Idempotent per park: the in_ready flag drops a redundant wake. */
void rf_sched_wake(rf_sched* s, rf_coro* c)
{
    if (s == NULL || c == NULL) {
        return;
    }
    rf_mutex_lock(&s->lock);
    if (!c->in_ready) {
        rf_sched_push_ready(s, c);
        rf_cond_signal(&s->cond);
    }
    rf_mutex_unlock(&s->lock);
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

    rf_mutex_lock(&s->lock);
    while (s->live > 0) {
        rf_coro* c = rf_sched_pop_ready(s);
        if (c != NULL) {
            /* Run user code WITHOUT the lock: it may re-enter the scheduler (spawn, park) and a
             * worker thread may want to wake someone meanwhile. The coroutine re-registers itself
             * (timer / external) before yielding, so it is not ready again until its wake fires. */
            rf_mutex_unlock(&s->lock);
            rf_coro_status st = rf_coro_resume(c);
            rf_mutex_lock(&s->lock);
            if (st == RF_CORO_COMPLETED) {
                s->live--; /* the owner (the Coroutine[T] handle) frees it later */
            }
            continue;
        }

        /* Nothing ready. Wait for the earliest timer if any, otherwise purely for an external
         * wake (a worker thread signalling). Timers are touched only on this thread, so reading
         * the head under the lock is fine. */
        if (s->timers != NULL) {
            uint64_t now = rf_now_ns();
            uint64_t deadline = s->timers->wake_ns;
            if (deadline > now) {
                rf_cond_wait_ns(&s->cond, &s->lock, deadline - now);
            }
            now = rf_now_ns();
            while (s->timers != NULL && s->timers->wake_ns <= now) {
                rf_coro* t = s->timers;
                s->timers = t->sched_next;
                t->sched_next = NULL;
                rf_sched_push_ready(s, t);
            }
        } else {
            /* live > 0 but nothing ready and no timer: everyone left is parked externally, so the
             * only thing that can make progress is a cross-thread wake. Block until it arrives. */
            rf_cond_wait_forever(&s->cond, &s->lock);
        }
    }
    rf_mutex_unlock(&s->lock);

    g_sched = prev;
}

/* True (1) when the caller is running inside a coroutine that is driven by a scheduler — i.e. a
 * park (rf_sched_park_timer) would actually suspend and let siblings run. False (0) on a plain
 * thread, or in a coroutine pumped without a scheduler (where a "park" could not be honored).
 * Lets `waitfor` be uncolored: park under a scheduler, OS-sleep otherwise. */
int rf_in_coroutine(void)
{
    return (g_sched != NULL && rf_coro_current() != NULL) ? 1 : 0;
}