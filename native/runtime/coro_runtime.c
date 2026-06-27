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

#include "rf_sync.h" /* portable rf_mutex / rf_cond — shared with the task↔coro bridge */

/* Runtime error + stack trace + exit(1) (stacktrace.c). We raise this on a hard allocation failure so
 * a coroutine that cannot be created dies with a diagnosed error instead of a NULL that crashes later
 * (or, at scale, a machine wedged under commit pressure). */
extern void __rf_throw(const char* error_type, const char* message);

/* Default coroutine stack, in bytes, when the caller passes stack_size == 0. The stack is a DEMAND-
 * COMMITTED mapping handed to co_derive (see rf_coro_stack_alloc): we reserve the whole region but
 * only commit pages as the stack actually grows into them, so this is a VIRTUAL reserve. A parked
 * coroutine therefore charges roughly the pages it has touched — NOT a full megabyte — against the
 * system commit limit; that is what lets a great many coroutines coexist without exhausting the
 * pagefile. A no-access guard page below the body turns an overflow into a clean fault instead of
 * silent neighbour corruption, so we can reserve generously (deep call chains safe). (Design §9.2.) */
#define RF_CORO_DEFAULT_STACK (1024u * 1024u)

/* We commit just the top PAGE of a fresh stack up front — enough for co_derive to lay down the
 * initial context without a fault — and let everything below commit on demand. Keeping the eager
 * commit to one page (not a fat window) is what holds per-coroutine commit charge to a few KiB, so
 * very many coroutines can coexist. (Windows: the handler below commits on touch; POSIX: the kernel
 * does, MAP_NORESERVE.) */

struct rf_coro {
    rf_context_entry_fn entry;     /* user routine to run inside the coroutine            */
    void* userdata;                /* opaque argument handed to entry                     */
    rf_coro_status status;         /* NEW -> RUNNING -> {PARKED -> RUNNING}* -> COMPLETED */
    rf_cancel_frame* cf_top;       /* top of the cancellation shadow stack (NULL = empty) */
    struct rf_coro* sched_next;    /* scheduler link: ready queue OR timer list (one at a time) */
    uint64_t wake_ns;              /* monotonic deadline this coroutine is parked until         */
    int in_ready;                  /* 1 while queued in the ready FIFO (dedups external wakes)  */
    int counted_done;              /* 1 once its completion has decremented live (idempotent)   */
#ifdef HAVE_LIBCO
    cothread_t thread;             /* libco context for this coroutine's own stack        */
    cothread_t resumer;            /* context to switch back to on yield/finish           */
    void* stack_region;            /* whole stack mapping (guard page included) for teardown     */
    size_t stack_region_size;      /* byte length of stack_region; 0 if libco malloc'd it itself */
#endif
};

#ifdef HAVE_LIBCO

/* Platform memory primitives for demand-paged coroutine stacks (rf_coro_stack_alloc). */
#if defined(_WIN32)
  #include <windows.h>
#else
  #include <sys/mman.h>
  #include <unistd.h>
#endif

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

#if defined(_WIN32)
/*
 * Demand-committed coroutine stacks (Windows).
 *
 * We RESERVE the whole stack region but COMMIT pages lazily, as the stack first touches them, via a
 * vectored exception handler. This is the fix for the machine wedging when many coroutines are
 * spawned: VirtualAlloc(MEM_COMMIT) charges every committed byte against the system commit limit
 * (pagefile-backed) up front, so N stacks of 1 MiB charge N MiB whether or not they are used —
 * spawn enough and the OS thrashes the pagefile toward the limit and the machine freezes. Reserving
 * costs only address space; committing on demand charges only the pages actually used (a parked
 * shallow coroutine ≈ a couple of pages).
 *
 * Region layout (page-aligned):
 *   [ header page | guard page | ... stack body, grows downward ... ]
 *   base           base+pg      base+2pg                       base+total
 *
 * The header page (committed) carries a magic + the body's commit range, so the handler can identify
 * OUR regions from a bare fault address in O(1) with no global registry: VirtualQuery yields the
 * AllocationBase (= header), we verify the magic, then commit the faulting page if it lies in the
 * body. A fault below the body (into the reserved guard page) is a genuine stack overflow and is
 * left for the normal handler to report.
 */
#define RF_CORO_STACK_MAGIC 0x52464353544B3031ULL /* "RFCSTK01" */

typedef struct {
    uint64_t  magic;
    uintptr_t commit_lo; /* first byte of the demand-committed body          */
    uintptr_t commit_hi; /* one past the last byte of the region (body top)  */
} rf_coro_stack_header;

static size_t g_rf_page = 0; /* cached system page size, set when the handler is installed */

/* Catch the access violation on the first touch of a reserved body page and commit it, so the stack
 * grows on demand. Strictly bounds-checked against our own regions (magic + range); anything else —
 * including faults the .NET host or the trace handler must see — passes through untouched. */
static LONG WINAPI rf_coro_commit_handler(EXCEPTION_POINTERS* ep)
{
    if (ep->ExceptionRecord->ExceptionCode != EXCEPTION_ACCESS_VIOLATION) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    uintptr_t fault = (uintptr_t)ep->ExceptionRecord->ExceptionInformation[1]; /* faulting address */

    MEMORY_BASIC_INFORMATION mbi;
    if (VirtualQuery((void*)fault, &mbi, sizeof(mbi)) == 0 || mbi.AllocationBase == NULL) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    /* The region base (header page) must itself be committed before we dereference it, so a fault in
     * unrelated memory can never make us read garbage and mistake it for one of ours. */
    void* base = mbi.AllocationBase;
    MEMORY_BASIC_INFORMATION hmbi;
    if (VirtualQuery(base, &hmbi, sizeof(hmbi)) == 0 || hmbi.State != MEM_COMMIT) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    rf_coro_stack_header* hdr = (rf_coro_stack_header*)base;
    if (hdr->magic != RF_CORO_STACK_MAGIC) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    if (fault < hdr->commit_lo || fault >= hdr->commit_hi) {
        return EXCEPTION_CONTINUE_SEARCH; /* into the guard page = real overflow; not ours to commit */
    }
    size_t pg = g_rf_page ? g_rf_page : 4096u;
    void* page = (void*)(fault & ~((uintptr_t)pg - 1));
    if (VirtualAlloc(page, pg, MEM_COMMIT, PAGE_READWRITE) == NULL) {
        return EXCEPTION_CONTINUE_SEARCH; /* commit limit truly exhausted — let it surface */
    }
    return EXCEPTION_CONTINUE_EXECUTION;
}

/* Install the demand-commit handler exactly once. First=1 so it runs before the trace handler and a
 * demand-commit fault never prints a spurious AccessViolation. */
static void rf_coro_install_commit_handler(void)
{
    static volatile LONG installed = 0;
    if (InterlockedCompareExchange(&installed, 1, 0) == 0) {
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        g_rf_page = (size_t)si.dwPageSize;
        AddVectoredExceptionHandler(1, rf_coro_commit_handler);
    }
}
#endif

/* Allocate a demand-committed coroutine stack of (at least) `usable` bytes and hand back the usable
 * region to give co_derive. A no-access GUARD PAGE sits just below the usable region (the stack
 * grows downward into it), so a stack overflow faults cleanly instead of silently scribbling on the
 * neighbouring allocation. Only the pages the stack actually touches are committed (Windows: via the
 * handler above; POSIX: by the kernel, MAP_NORESERVE), so a generous reserve is cheap. *region /
 * *region_size capture the WHOLE mapping (header + guard included) for rf_coro_stack_free. Returns
 * NULL on failure (the caller raises a runtime error).
 *
 * All targets reach here: every libco backend we build (amd64.c / aarch64.c, including the Windows
 * clang-cl build) uses a caller-supplied buffer via co_derive — only the Windows fiber backend
 * refuses one, and we never select it. */
static void* rf_coro_stack_alloc(size_t usable, void** region, size_t* region_size)
{
#if defined(_WIN32)
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    size_t pg = (size_t)si.dwPageSize;
    size_t header = pg;
    size_t guard = pg;
    size_t body = (usable + pg - 1) & ~(pg - 1);
    size_t total = header + guard + body;

    /* RESERVE only: address space, no commit charge / no pagefile backing until pages are touched. */
    char* base = (char*)VirtualAlloc(NULL, total, MEM_RESERVE, PAGE_NOACCESS);
    if (base == NULL) {
        return NULL;
    }
    /* Commit + stamp the header page so the commit handler can recognise this region. */
    if (VirtualAlloc(base, header, MEM_COMMIT, PAGE_READWRITE) == NULL) {
        VirtualFree(base, 0, MEM_RELEASE);
        return NULL;
    }
    rf_coro_stack_header* hdr = (rf_coro_stack_header*)base;
    hdr->magic     = RF_CORO_STACK_MAGIC;
    hdr->commit_lo = (uintptr_t)(base + header + guard);
    hdr->commit_hi = (uintptr_t)(base + total);

    /* Commit just the TOP page (where the stack starts) so co_derive's initial context write doesn't
     * fault; the guard page stays reserved (overflow → fault → reported), the rest commits on demand
     * as the stack grows — so a parked coroutine charges only the pages it has actually used. */
    size_t initial = body < pg ? body : pg;
    if (VirtualAlloc(base + total - initial, initial, MEM_COMMIT, PAGE_READWRITE) == NULL) {
        VirtualFree(base, 0, MEM_RELEASE);
        return NULL;
    }
    rf_coro_install_commit_handler();

    *region = base;
    *region_size = total;
    return base + header + guard; /* usable body start */
#else
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
#endif
}

/* Release a stack mapping from rf_coro_stack_alloc. NOT co_delete: that frees the libco handle with
 * LIBCO_FREE (plain free), but our stack is an mmap / VirtualAlloc region, not malloc'd. */
static void rf_coro_stack_free(void* region, size_t region_size)
{
    if (region == NULL) {
        return;
    }
#if defined(_WIN32)
    (void)region_size;
    VirtualFree(region, 0, MEM_RELEASE);
#else
    munmap(region, region_size);
#endif
}
#endif

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

#ifdef HAVE_LIBCO
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

/* Pure context switch back to the resumer, marking PARKED. The low-level half of every suspend:
 * the caller is responsible for arranging WHEN the coroutine becomes runnable again (a timer, an
 * external wake, or re-queuing to ready for a cooperative yield). NOT scheduler-aware itself.
 * The public rf_coro_yield (cooperative) is defined after the scheduler, since it re-queues. */
static void rf_coro_switch_out(void)
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
        /* Release the stack mapping ourselves (guard page included). We must NOT co_delete it:
         * co_delete frees the handle with plain free(), but our stack came from mmap/VirtualAlloc. */
        rf_coro_stack_free(coro->stack_region, coro->stack_region_size);
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
 * libco-gated g_current_coro so the scheduler (compiled regardless of HAVE_LIBCO) and the
 * task↔coro await bridge (task_runtime.c) can recover the running coroutine. */
rf_coro* rf_coro_current(void)
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

/* Insert into the timer list keeping it sorted by wake_ns ascending (earliest first). Caller holds
 * s->lock — the timer list is mutated from the scheduler thread (park) AND, for deadline parks that
 * are also externally wakeable, unlinked by rf_sched_wake from a worker thread. */
static void rf_sched_insert_timer(rf_sched* s, rf_coro* c)
{
    rf_coro** link = &s->timers;
    while (*link != NULL && (*link)->wake_ns <= c->wake_ns) {
        link = &(*link)->sched_next;
    }
    c->sched_next = *link;
    *link = c;
}

/* Remove `c` from the timer list if present (caller holds s->lock). Used when a deadline-parked
 * coroutine is woken by something OTHER than its timer (a worker completing the awaited task), so
 * it does not linger in the timer list and get moved to ready a second time when the timer fires. */
static void rf_sched_unlink_timer(rf_sched* s, rf_coro* c)
{
    rf_coro** link = &s->timers;
    while (*link != NULL) {
        if (*link == c) {
            *link = c->sched_next;
            c->sched_next = NULL;
            return;
        }
        link = &(*link)->sched_next;
    }
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
    rf_mutex_lock(&s->lock);
    self->wake_ns = rf_now_ns() + delay_ns;
    rf_sched_insert_timer(s, self);
    rf_mutex_unlock(&s->lock);
    rf_coro_switch_out(); /* switch back to the run loop; it resumes us when the timer fires */
}

/* Park the current coroutine until `delay_ns` from now — BUT, unlike rf_sched_park_timer, it also
 * stays externally wakeable (rf_sched_wake). The run loop resumes it whichever happens first: the
 * timer fires, or a worker thread wakes it (which unlinks it from the timer list). The substrate
 * for a timed await — racing an awaited task's completion against a deadline without blocking the
 * thread. The caller (rf_task_await_coro_deadline) re-checks both conditions after each resume. */
void rf_sched_park_deadline(uint64_t delay_ns)
{
    rf_sched* s = g_sched;
    rf_coro* self = rf_coro_current();
    if (s == NULL || self == NULL) {
        return;
    }
    rf_mutex_lock(&s->lock);
    self->wake_ns = rf_now_ns() + delay_ns;
    rf_sched_insert_timer(s, self);
    rf_mutex_unlock(&s->lock);
    rf_coro_switch_out();
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
    rf_coro_switch_out(); /* the run loop will not resume us until someone calls rf_sched_wake */
}

/* Cooperative yield (the public suspend primitive): let other ready coroutines run, then continue.
 * Under a scheduler this re-queues the current coroutine to the ready FIFO before switching out, so
 * the run loop resumes it after the others — UNLIKE rf_sched_park_*, which wait for an external
 * condition. Outside a scheduler it is a bare switch back to the resumer (a naive pump re-resumes).
 * This is the primitive the may-suspend analysis seeds on. */
void rf_coro_yield(void)
{
#ifdef HAVE_LIBCO
    rf_coro* self = g_current_coro;
    if (self == NULL) {
        return; /* not inside a coroutine */
    }
    rf_sched* s = g_sched;
    if (s != NULL) {
        /* Re-queue self so the run loop resumes us again after the other ready coroutines. The
         * run loop holds no lock while we run, so taking it here is safe. */
        rf_mutex_lock(&s->lock);
        if (!self->in_ready) {
            rf_sched_push_ready(s, self);
        }
        rf_mutex_unlock(&s->lock);
    }
    rf_coro_switch_out();
#endif
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
    /* If it was parked on a deadline timer (rf_sched_park_deadline), unlink it first so the timer
     * does not later move an already-ready coroutine to ready a second time. No-op for a plain
     * external park (not in the timer list). */
    rf_sched_unlink_timer(s, c);
    if (!c->in_ready) {
        rf_sched_push_ready(s, c);
        rf_cond_signal(&s->cond);
    }
    rf_mutex_unlock(&s->lock);
}

/* Resume one ready coroutine if any (returns 1), else block until something can make progress and
 * return 0. Caller holds s->lock; this drops it around the resume / the cond wait and re-takes it.
 * The shared body of rf_sched_run and rf_sched_run_until. */
static int rf_sched_step(rf_sched* s)
{
    rf_coro* c = rf_sched_pop_ready(s);
    if (c != NULL) {
        /* Run user code WITHOUT the lock: it may re-enter the scheduler (spawn, park) and a
         * worker thread may want to wake someone meanwhile. The coroutine re-registers itself
         * (timer / external) before yielding, so it is not ready again until its wake fires. */
        rf_mutex_unlock(&s->lock);
        rf_coro_status st = rf_coro_resume(c);
        rf_mutex_lock(&s->lock);
        if (st == RF_CORO_COMPLETED && !c->counted_done) {
            c->counted_done = 1; /* count each completion exactly once, even if a stray wake */
            s->live--;           /* re-queued it after it finished (the owner frees it later) */
        }
        return 1;
    }

    /* Nothing ready. Wait for the earliest timer if any, otherwise purely for an external wake (a
     * worker thread signalling). Timers are touched only on this thread, so reading the head under
     * the lock is fine. */
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
        /* Nothing ready and no timer: everyone left is parked externally, so the only thing that
         * can make progress is a cross-thread wake. Block until it arrives. */
        rf_cond_wait_forever(&s->cond, &s->lock);
    }
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

    rf_mutex_lock(&s->lock);
    while (s->live > 0) {
        rf_sched_step(s);
    }
    rf_mutex_unlock(&s->lock);

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

    rf_mutex_lock(&s->lock);
    /* `target` counts toward `live` until it finishes, so while it is unfinished there is always
     * something to wait on; rf_sched_step never busy-spins. */
    while (target->status != RF_CORO_COMPLETED && target->status != RF_CORO_CANCELLED) {
        rf_sched_step(s);
    }
    rf_mutex_unlock(&s->lock);

    g_sched = prev;
}

/* Wake a scheduler's run loop without targeting a specific coroutine: just signal its cond. Used by
 * a worker thread completing a task that a `race!` loop is waiting on — the loop holds no awaiter
 * coroutine, it re-polls all competitors under s->lock when woken. Safe to call from ANY thread. */
void rf_sched_signal(rf_sched* s)
{
    if (s == NULL) {
        return;
    }
    rf_mutex_lock(&s->lock);
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
            return;
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

/* Drive the set until one competitor completes; return its index in add (List) order, or -1 if the
 * set is empty. Registers every thread competitor's race_sched so its completion wakes this loop. */
intptr_t rf_race_wait(rf_race* r)
{
    if (r == NULL || r->count == 0) {
        return -1;
    }
    rf_sched* s = rf_sched_thread_default();
    rf_sched* prev = g_sched;
    g_sched = s;

    for (intptr_t i = 0; i < r->count; i++) {
        if (r->kinds[i] == 1) {
            rf_task_race_register((rf_task*)r->handles[i], s);
        }
    }

    intptr_t winner = -1;
    rf_mutex_lock(&s->lock);
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
        /* Nothing done yet: progress coroutine competitors (and any siblings) and/or block on the
         * cond until a timer fires or a thread competitor signals us. Re-poll on return. */
        rf_sched_step(s);
    }
    rf_mutex_unlock(&s->lock);

    for (intptr_t i = 0; i < r->count; i++) {
        if (r->kinds[i] == 1) {
            rf_task_race_register((rf_task*)r->handles[i], NULL);
        }
    }

    g_sched = prev;
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

/* ---- Implicit per-thread scheduler (the `retrieve!` entry into async) --------------------- */
/*
 * Calling a `suspended routine` spawns its coroutine onto THIS thread's implicit scheduler (created
 * lazily, reused across calls), and Coroutine[T].retrieve!() drives that scheduler until the
 * handle is done. So a program enters async simply by retrieving a coroutine — no explicit run
 * loop — and coroutines spawned earlier on the thread run concurrently while one is retrieved.
 */
static _Thread_local rf_sched* g_thread_sched = NULL;

/* This thread's implicit scheduler, created on first use. Leaks one scheduler per thread that ever
 * runs a coroutine (negligible; a thread-exit reclaim is a later refinement). */
rf_sched* rf_sched_thread_default(void)
{
    if (g_thread_sched == NULL) {
        g_thread_sched = rf_sched_create();
    }
    return g_thread_sched;
}

/* Spawn a NEW coroutine onto this thread's implicit scheduler. Emitted at a `suspended routine`
 * call site right after rf_coro_create, so the coroutine is ready before any retrieve! drives. */
void rf_sched_spawn_default(rf_coro* c)
{
    rf_sched_spawn(rf_sched_thread_default(), c);
}

/* Drive this thread's implicit scheduler until `target` finishes. The retrieve!-side convenience
 * over rf_sched_run_until that needs no scheduler handle. */
void rf_sched_run_until_default(rf_coro* target)
{
    rf_sched_run_until(rf_sched_thread_default(), target);
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

    /* ready FIFO */
    rf_coro** rlink = &s->ready_head;
    rf_coro* prev = NULL;
    while (*rlink != NULL) {
        if (*rlink == c) {
            *rlink = c->sched_next;
            if (s->ready_tail == c) {
                s->ready_tail = prev;
            }
            c->sched_next = NULL;
            c->in_ready = 0;
            found = 1;
            break;
        }
        prev = *rlink;
        rlink = &(*rlink)->sched_next;
    }

    /* timer list (only if not found in ready — a coroutine is in at most one) */
    if (!found) {
        rf_coro** tlink = &s->timers;
        while (*tlink != NULL) {
            if (*tlink == c) {
                *tlink = c->sched_next;
                c->sched_next = NULL;
                found = 1;
                break;
            }
            tlink = &(*tlink)->sched_next;
        }
    }

    if (found) {
        s->live--;
    }
    rf_mutex_unlock(&s->lock);
    return found;
}

/* Detach `c` from this thread's implicit scheduler (if present). The $destroy-side convenience over
 * rf_sched_remove. Call BEFORE rf_coro_abandon on a coroutine that was spawned but never retrieved. */
int rf_sched_unschedule_default(rf_coro* c)
{
    return rf_sched_remove(g_thread_sched, c);
}

/* The monotonic nanosecond clock the scheduler uses for timers, exposed for the task↔coro deadline
 * bridge (so a timed await measures its deadline on the same clock the timer list is sorted by). */
uint64_t rf_monotonic_now_ns(void)
{
    return rf_now_ns();
}