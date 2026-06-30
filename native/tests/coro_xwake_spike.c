/*
 * coro_xwake_spike.c — cross-thread wake harness (no RF front-end).
 *
 * Proves the threaded <-> suspended bridge: a coroutine can await work running on ANOTHER OS
 * thread without blocking the scheduler thread. The "awaiter" coroutine arms a real worker thread,
 * then parks externally (rf_sched_park_external) — it is neither ready nor on a timer, so the only
 * thing that can re-queue it is the worker calling rf_sched_wake from its own thread. Meanwhile a
 * "sibling" coroutine keeps ticking on timers, demonstrating the scheduler thread stays live while
 * the awaiter waits. When the worker finishes (~60 ms of blocking work) it publishes a result and
 * wakes the awaiter, which reads the result and finishes.
 *
 * Assertions:
 *   - the awaiter receives the worker's result (no lost wake, no race),
 *   - the sibling ticked several times WHILE the awaiter was parked (concurrent, not blocked),
 *   - total wall time ≈ the worker's blocking time, not serialized on top of the sibling.
 *
 * Build (Windows x64, clang):
 *   clang -std=c23 -I native/include -I native/libco -DHAVE_LIBCO \
 *       native/tests/coro_xwake_spike.c native/runtime/coro_runtime.c \
 *       native/runtime/concurrency_context.c native/libco/libco.c -o build/coro_xwake_spike.exe
 * (POSIX: append -lpthread.)
 *
 * Exit code 0 = all assertions passed.
 */

#include "razorforge_runtime.h"

#include <stdio.h>
#include <stdint.h>

/* ---- portable OS-thread spawn + sleep (spike-local; the runtime has its own) -------------- */
#ifdef _WIN32
#include <windows.h>
typedef HANDLE xthread_t;
static void sleep_ms(unsigned ms) { Sleep(ms); }
static uint64_t now_ms(void)
{
    static LARGE_INTEGER f;
    if (f.QuadPart == 0) QueryPerformanceFrequency(&f);
    LARGE_INTEGER c; QueryPerformanceCounter(&c);
    return (uint64_t)((c.QuadPart * 1000ull) / (uint64_t)f.QuadPart);
}
static DWORD WINAPI win_trampoline(LPVOID arg);
typedef void (*body_fn)(void*);
typedef struct { body_fn fn; void* arg; } thunk;
static thunk g_thunk;
static xthread_t start_thread(body_fn fn, void* arg)
{
    g_thunk.fn = fn; g_thunk.arg = arg;
    return CreateThread(NULL, 0, win_trampoline, &g_thunk, 0, NULL);
}
static DWORD WINAPI win_trampoline(LPVOID arg)
{
    thunk* t = (thunk*)arg;
    t->fn(t->arg);
    return 0;
}
static void join_thread(xthread_t th) { WaitForSingleObject(th, INFINITE); CloseHandle(th); }
#else
#include <pthread.h>
#include <time.h>
typedef pthread_t xthread_t;
static void sleep_ms(unsigned ms)
{
    struct timespec ts; ts.tv_sec = ms / 1000; ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
}
static uint64_t now_ms(void)
{
    struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000ull + (uint64_t)ts.tv_nsec / 1000000ull;
}
typedef void (*body_fn)(void*);
typedef struct { body_fn fn; void* arg; } thunk;
static thunk g_thunk;
static void* posix_trampoline(void* arg) { thunk* t = (thunk*)arg; t->fn(t->arg); return NULL; }
static xthread_t start_thread(body_fn fn, void* arg)
{
    g_thunk.fn = fn; g_thunk.arg = arg;
    pthread_t th; pthread_create(&th, NULL, posix_trampoline, &g_thunk); return th;
}
static void join_thread(xthread_t th) { pthread_join(th, NULL); }
#endif

#define CHECK(cond, msg)                                                        \
    do {                                                                        \
        if (!(cond)) {                                                          \
            fprintf(stderr, "FAIL: %s  (%s:%d)\n", (msg), __FILE__, __LINE__);  \
            return 1;                                                           \
        }                                                                       \
    } while (0)

/* Shared state the worker thread and the awaiter coroutine communicate through. */
typedef struct {
    rf_sched* s;
    rf_coro* self;   /* the awaiter coroutine's own handle (filled in by main after create) */
    int result;      /* the worker publishes its result here before waking the awaiter      */
} awaited;

static xthread_t g_worker;

/* Runs on a SEPARATE OS thread: do ~60 ms of blocking work, publish a result, wake the awaiter. */
static void worker_body(void* arg)
{
    awaited* a = (awaited*)arg;
    sleep_ms(60);
    a->result = 42;
    rf_sched_wake(a->s, a->self);  /* cross-thread: re-queues the parked coroutine + signals */
}

static int g_sibling_ticks;        /* incremented each time the sibling wakes from its timer */
static int g_awaiter_result;       /* what the awaiter read after being woken                */
static int g_ticks_at_wake;        /* sibling ticks observed at the moment the awaiter resumed */

/* Awaits the worker thread: arm it, then park with no scheduler-side wake condition. */
static void awaiter_body(void* ud)
{
    awaited* a = (awaited*)ud;
    g_worker = start_thread(worker_body, a);   /* arm the waker BEFORE parking */
    rf_sched_park_external();                  /* parked until rf_sched_wake; thread not blocked */
    g_awaiter_result = a->result;              /* woken: the worker's result is ready */
    g_ticks_at_wake = g_sibling_ticks;
}

/* A sibling coroutine that keeps making progress (5 timer ticks ~15 ms apart) while the awaiter
 * is parked — proof the scheduler thread is not blocked on the worker. */
static void sibling_body(void* ud)
{
    (void)ud;
    for (int i = 0; i < 5; i++) {
        rf_sched_park_timer(15ull * 1000000ull);
        g_sibling_ticks++;
    }
}

int main(void)
{
    printf("coro backend: %s\n", rf_context_backend_name());

    rf_sched* s = rf_sched_create();
    CHECK(s != NULL, "sched create failed");

    awaited a = { .s = s, .self = NULL, .result = 0 };

    rf_coro* c_await = rf_coro_create(awaiter_body, &a, 0);
    rf_coro* c_sib = rf_coro_create(sibling_body, NULL, 0);
    CHECK(c_await && c_sib, "coro create failed");
    a.self = c_await;   /* now that the handle exists, let the worker target it */

    rf_sched_spawn(s, c_await);
    rf_sched_spawn(s, c_sib);

    uint64_t t0 = now_ms();
    rf_sched_run(s);              /* drives both; blocks on the cond when only the awaiter is parked */
    uint64_t elapsed = now_ms() - t0;

    join_thread(g_worker);

    printf("awaiter result: %d; sibling ticks total: %d; ticks at wake: %d; total ~%llu ms\n",
           g_awaiter_result, g_sibling_ticks, g_ticks_at_wake, (unsigned long long)elapsed);

    CHECK(g_awaiter_result == 42, "awaiter should receive the worker's cross-thread result");
    CHECK(g_sibling_ticks == 5, "sibling should complete all its ticks");
    /* The worker takes ~60 ms; the sibling ticks every ~15 ms. By the time the worker wakes the
     * awaiter, the sibling must have ticked at least a couple of times — i.e. it ran concurrently
     * while the awaiter was parked, rather than the scheduler thread being blocked. */
    CHECK(g_ticks_at_wake >= 2, "sibling must run while the awaiter is parked (concurrency)");
    /* Total ≈ max(worker 60 ms, sibling 5*15=75 ms) ≈ 75 ms, NOT 60+75 serialized. */
    CHECK(elapsed < 120, "total should be ~max, not worker+sibling serialized");

    rf_coro_delete(c_await);
    rf_coro_delete(c_sib);
    rf_sched_destroy(s);

    printf("OK: coroutine awaited a worker thread without blocking the scheduler\n");
    return 0;
}
