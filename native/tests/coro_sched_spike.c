/*
 * coro_sched_spike.c — scheduler run-loop harness (no RF front-end).
 *
 * Proves the cooperative scheduler interleaves multiple coroutines on ONE OS thread via timer
 * parking: two workers park on different delays, and the scheduler resumes each when its timer
 * fires. The shorter-delay worker finishes first, and total wall time ≈ max(delays), NOT the sum —
 * i.e. they ran concurrently, not serially. That is the core of `waitfor` parking instead of
 * blocking the thread.
 *
 * Build (Windows x64, clang):
 *   clang -std=c23 -I native/include -I native/libco -DHAVE_LIBCO \
 *       native/tests/coro_sched_spike.c native/runtime/coro_runtime.c \
 *       native/runtime/concurrency_context.c native/libco/libco.c -o build/coro_sched_spike.exe
 *
 * Exit code 0 = all assertions passed.
 */

#include "razorforge_runtime.h"

#include <stdio.h>
#include <stdint.h>

/* Records the order workers finish, so we can assert the scheduler ran the shorter one first. */
static int finish_order[4];
static int finish_count;

typedef struct { int id; uint64_t delay_ns; } work;

static void worker(void* ud)
{
    work* w = (work*)ud;
    rf_sched_park_timer(w->delay_ns);   /* park; the loop resumes us when the timer fires */
    finish_order[finish_count++] = w->id;
}

#define CHECK(cond, msg)                                                    \
    do {                                                                    \
        if (!(cond)) {                                                      \
            fprintf(stderr, "FAIL: %s  (%s:%d)\n", (msg), __FILE__, __LINE__); \
            return 1;                                                       \
        }                                                                   \
    } while (0)

static uint64_t now_ms(void);

int main(void)
{
    printf("coro backend: %s\n", rf_context_backend_name());

    /* worker 1 waits 120 ms, worker 2 waits 40 ms — spawned in 1,2 order. */
    work w1 = { .id = 1, .delay_ns = 120ull * 1000000ull };
    work w2 = { .id = 2, .delay_ns = 40ull * 1000000ull };

    rf_sched* s = rf_sched_create();
    CHECK(s != NULL, "sched create failed");

    rf_coro* c1 = rf_coro_create(worker, &w1, 0);
    rf_coro* c2 = rf_coro_create(worker, &w2, 0);
    CHECK(c1 && c2, "coro create failed");

    rf_sched_spawn(s, c1);
    rf_sched_spawn(s, c2);

    uint64_t t0 = now_ms();
    rf_sched_run(s);   /* drives both to completion on this one thread */
    uint64_t elapsed = now_ms() - t0;

    printf("finish order: %d then %d; total ~%llu ms\n",
           finish_order[0], finish_order[1], (unsigned long long)elapsed);

    /* The 40 ms worker (id 2) must finish before the 120 ms worker (id 1). */
    CHECK(finish_count == 2, "both workers should finish");
    CHECK(finish_order[0] == 2 && finish_order[1] == 1, "shorter delay should finish first");
    /* Concurrent: total ≈ max(120, 40) = 120, NOT 160. Allow generous slack for CI jitter. */
    CHECK(elapsed >= 110 && elapsed < 160, "total should be ~max(delays), not the sum");

    rf_coro_delete(c1);
    rf_coro_delete(c2);
    rf_sched_destroy(s);

    printf("OK: scheduler interleaved two coroutines concurrently\n");
    return 0;
}

#ifdef _WIN32
#include <windows.h>
static uint64_t now_ms(void)
{
    static LARGE_INTEGER f;
    if (f.QuadPart == 0) QueryPerformanceFrequency(&f);
    LARGE_INTEGER c; QueryPerformanceCounter(&c);
    return (uint64_t)((c.QuadPart * 1000ull) / (uint64_t)f.QuadPart);
}
#else
#include <time.h>
static uint64_t now_ms(void)
{
    struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000ull + (uint64_t)ts.tv_nsec / 1000000ull;
}
#endif
