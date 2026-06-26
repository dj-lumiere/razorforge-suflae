/*
 * coro_async_spike.c — the v0.2.0 "full async" surface, de-risked end to end (no RF front-end).
 *
 * Two things the implicit-scheduler retrieve! surface relies on:
 *
 *  (1) run-until-this-handle: rf_sched_run_until drives the loop only until ONE coroutine finishes,
 *      leaving siblings parked for later — siblings still progress concurrently meanwhile. We spawn
 *      a fast coro (1 short timer) and a slow coro (3 timers); run_until(fast) returns with fast
 *      done while slow is only partway, then run_until(slow) finishes it.
 *
 *  (2) a coroutine awaiting a threaded Task without blocking the scheduler thread: an awaiter coro
 *      registers on a real rf_task (rf_task_await_coro) and parks; the task's worker thread does
 *      ~50ms of work, completes the task, and the completion path wakes the awaiter via the
 *      cross-thread bridge. A sibling coro keeps ticking while the awaiter is parked.
 *
 * Build (Windows x64, clang):
 *   clang -std=c23 -I native/include -I native/libco -DHAVE_LIBCO \
 *       native/tests/coro_async_spike.c native/runtime/coro_runtime.c \
 *       native/runtime/task_runtime.c native/runtime/concurrency_context.c \
 *       native/libco/libco.c -o build/coro_async_spike.exe
 * (POSIX: append -lpthread.)
 *
 * Exit code 0 = all assertions passed.
 */

#include "razorforge_runtime.h"

#include <stdio.h>
#include <stdint.h>

#define CHECK(cond, msg)                                                        \
    do {                                                                        \
        if (!(cond)) {                                                          \
            fprintf(stderr, "FAIL: %s  (%s:%d)\n", (msg), __FILE__, __LINE__);  \
            return 1;                                                           \
        }                                                                       \
    } while (0)

/* ---- (1) run-until-this-handle ------------------------------------------------------------ */

static int g_fast_done, g_slow_done, g_slow_ticks;

static void fast_coro(void* ud)
{
    (void)ud;
    rf_sched_park_timer(40ull * 1000000ull);   /* finishes well after slow's first few ticks */
    g_fast_done = 1;
}

static void slow_coro(void* ud)
{
    (void)ud;
    for (int i = 0; i < 10; i++) {             /* 10 * 8ms = 80ms total — outlives fast's 40ms */
        rf_sched_park_timer(8ull * 1000000ull);
        g_slow_ticks++;
    }
    g_slow_done = 1;
}

static int test_run_until(void)
{
    rf_sched* s = rf_sched_create();
    CHECK(s != NULL, "sched create");

    rf_coro* fast = rf_coro_create(fast_coro, NULL, 0);
    rf_coro* slow = rf_coro_create(slow_coro, NULL, 0);
    CHECK(fast && slow, "coro create");

    rf_sched_spawn(s, fast);
    rf_sched_spawn(s, slow);

    rf_sched_run_until(s, fast);   /* returns as soon as `fast` finishes */
    CHECK(g_fast_done == 1, "run_until should finish the target");
    CHECK(g_slow_done == 0, "run_until must NOT force the sibling to completion");
    CHECK(g_slow_ticks >= 1, "sibling should have progressed concurrently while target ran");

    rf_sched_run_until(s, slow);   /* now drive the leftover to completion */
    CHECK(g_slow_done == 1, "second run_until finishes the sibling");

    rf_coro_delete(fast);
    rf_coro_delete(slow);
    rf_sched_destroy(s);
    printf("OK (1): run_until finished target only; sibling ticked %d then completed later\n",
           g_slow_ticks);
    return 0;
}

/* ---- (2) coroutine awaits a threaded task ------------------------------------------------- */

#ifdef _WIN32
#include <windows.h>
static void sleep_ms(unsigned ms) { Sleep(ms); }
#else
#include <time.h>
static void sleep_ms(unsigned ms)
{
    struct timespec ts; ts.tv_sec = ms / 1000; ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
}
#endif

static int g_payload = 1234;       /* the task's "result" (we only check identity, not boxing) */
static int g_awaiter_done, g_awaiter_saw_payload, g_ticks_at_await_wake;
static int g_sib_ticks;

/* Threaded task body (runs on its OWN OS thread, spawned by rf_task_spawn_threaded). */
static void task_entry(rf_task* task, void* ud)
{
    (void)ud;
    sleep_ms(50);
    rf_task_complete_value(task, &g_payload);   /* completion path wakes the awaiter coro */
}

typedef struct { rf_task* task; } await_arg;

static void awaiter_coro(void* ud)
{
    await_arg* a = (await_arg*)ud;
    /* Park until the task completes; re-checks under the lock so no wake is lost. */
    while (rf_task_await_coro(a->task) == 0) {
        rf_sched_park_external();
    }
    void* p = rf_task_result_payload(a->task);
    g_awaiter_saw_payload = (p == &g_payload);
    g_ticks_at_await_wake = g_sib_ticks;
    g_awaiter_done = 1;
}

static void sib_coro(void* ud)
{
    (void)ud;
    for (int i = 0; i < 4; i++) {
        rf_sched_park_timer(15ull * 1000000ull);
        g_sib_ticks++;
    }
}

static int test_await_threaded(void)
{
    rf_sched* s = rf_sched_create();
    CHECK(s != NULL, "sched create");

    rf_task* task = rf_task_create(RF_TASK_THREADED);
    CHECK(task != NULL, "task create");
    CHECK(rf_task_spawn_threaded(task, task_entry, NULL) != 0, "task spawn");

    await_arg aa = { .task = task };
    rf_coro* awaiter = rf_coro_create(awaiter_coro, &aa, 0);
    rf_coro* sib = rf_coro_create(sib_coro, NULL, 0);
    CHECK(awaiter && sib, "coro create");

    rf_sched_spawn(s, awaiter);
    rf_sched_spawn(s, sib);

    rf_sched_run_until(s, awaiter);   /* awaiter parks on the task; sibling ticks; worker wakes it */
    CHECK(g_awaiter_done == 1, "awaiter should complete");
    CHECK(g_awaiter_saw_payload == 1, "awaiter should read the task's result payload");
    CHECK(g_ticks_at_await_wake >= 2, "sibling must tick while the awaiter is parked (concurrency)");

    rf_sched_run_until(s, sib);       /* finish the sibling's remaining ticks */

    rf_task_destroy(task);
    rf_coro_delete(awaiter);
    rf_coro_delete(sib);
    rf_sched_destroy(s);
    printf("OK (2): coroutine awaited a threaded task without blocking; sibling ticked %d at wake\n",
           g_ticks_at_await_wake);
    return 0;
}

int main(void)
{
    printf("coro backend: %s\n", rf_context_backend_name());
    int rc = test_run_until();
    if (rc) return rc;
    rc = test_await_threaded();
    if (rc) return rc;
    printf("OK: implicit-scheduler async surface (run_until + threaded await) works\n");
    return 0;
}
