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
};

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

static void rf_sleep_ns(uint64_t ns)
{
#ifdef _WIN32
    DWORD ms = (DWORD)((ns + 999999ULL) / 1000000ULL);
    if (ms > 0) Sleep(ms);
#else
    struct timespec req;
    req.tv_sec = (time_t)(ns / 1000000000ULL);
    req.tv_nsec = (long)(ns % 1000000000ULL);
    while (nanosleep(&req, &req) == -1 && errno == EINTR) { }
#endif
}

static void rf_sched_push_ready(rf_sched* s, rf_coro* c)
{
    c->sched_next = NULL;
    if (s->ready_tail != NULL) {
        s->ready_tail->sched_next = c;
    } else {
        s->ready_head = c;
    }
    s->ready_tail = c;
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
    return (rf_sched*)calloc(1, sizeof(rf_sched));
}

void rf_sched_destroy(rf_sched* s)
{
    free(s);
}

/* Queue a (newly created, NEW) coroutine to run. It starts on its first resume by the loop. */
void rf_sched_spawn(rf_sched* s, rf_coro* c)
{
    if (s == NULL || c == NULL) {
        return;
    }
    rf_sched_push_ready(s, c);
    s->live++;
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

/* Drive all spawned coroutines to completion on this thread. */
void rf_sched_run(rf_sched* s)
{
    if (s == NULL) {
        return;
    }
    rf_sched* prev = g_sched;
    g_sched = s;

    while (s->live > 0) {
        /* Resume everything currently ready. A coroutine that parks re-registers itself (timer)
         * before yielding, so it is not ready again until its condition fires. */
        while (s->ready_head != NULL) {
            rf_coro* c = s->ready_head;
            s->ready_head = c->sched_next;
            if (s->ready_head == NULL) {
                s->ready_tail = NULL;
            }
            c->sched_next = NULL;

            if (rf_coro_resume(c) == RF_CORO_COMPLETED) {
                s->live--; /* the owner (the Coroutine[T] handle) frees it later */
            }
        }

        if (s->timers == NULL) {
            break; /* nothing ready and nothing waiting — done (or a stall) */
        }

        /* Sleep until the earliest deadline, then move every expired timer to ready. */
        uint64_t now = rf_now_ns();
        uint64_t deadline = s->timers->wake_ns;
        if (deadline > now) {
            rf_sleep_ns(deadline - now);
        }
        now = rf_now_ns();
        while (s->timers != NULL && s->timers->wake_ns <= now) {
            rf_coro* c = s->timers;
            s->timers = c->sched_next;
            c->sched_next = NULL;
            rf_sched_push_ready(s, c);
        }
    }

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