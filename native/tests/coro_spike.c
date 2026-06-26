/*
 * coro_spike.c — Phase 1 context-switch spike harness (no RF front-end).
 *
 * Proves the v0.2.0 coroutine substrate (rf_coro_create/resume/yield/delete) actually
 * context-switches: a coroutine deep in a counted loop parks via rf_coro_yield, the host
 * resumes it repeatedly, observes interleaved progress, and sees it finish exactly once.
 *
 * Build (Windows x64, clang — uses libco amd64.c real assembly switch):
 *   clang -std=c23 -I native/include -I native/libco -DHAVE_LIBCO \
 *       native/tests/coro_spike.c native/runtime/coro_runtime.c native/libco/libco.c \
 *       -o build/coro_spike.exe
 *
 * Exit code 0 = all assertions passed.
 */

#include "razorforge_runtime.h"

#include <stdio.h>

/* Shared state between host and coroutine. The coroutine increments `ticks` once per
 * resumption, yielding after each, then sets `done` and returns on the last. */
typedef struct {
    int target;   /* how many yields before finishing            */
    int ticks;    /* incremented by the coroutine each step      */
    int reentry;  /* how many times the body resumed past a yield */
    int done;     /* set to 1 right before the entry returns      */
} spike_state;

static void spike_body(void* userdata)
{
    spike_state* s = (spike_state*)userdata;
    for (int i = 0; i < s->target; i++) {
        s->ticks += 1;
        rf_coro_yield();   /* park; control returns to whoever resumed us */
        s->reentry += 1;   /* observed only after a successful resume     */
    }
    s->done = 1;
}

#define CHECK(cond, msg)                                                    \
    do {                                                                    \
        if (!(cond)) {                                                      \
            fprintf(stderr, "FAIL: %s  (%s:%d)\n", (msg), __FILE__, __LINE__); \
            return 1;                                                       \
        }                                                                   \
    } while (0)

int main(void)
{
    printf("coro backend: %s\n", rf_context_backend_name());

    spike_state st = { .target = 4, .ticks = 0, .reentry = 0, .done = 0 };

    rf_coro* coro = rf_coro_create(spike_body, &st, 0);
    CHECK(coro != NULL, "rf_coro_create returned NULL");
    CHECK(rf_coro_status_get(coro) == RF_CORO_NEW, "fresh coroutine should be NEW");

    /* Drive it: each resume runs one loop iteration then parks. We expect `target`
     * parks, then a final resume that runs the loop tail and completes. */
    int resumes = 0;
    for (;;) {
        rf_coro_status status = rf_coro_resume(coro);
        resumes += 1;

        if (status == RF_CORO_COMPLETED) {
            break;
        }
        CHECK(status == RF_CORO_PARKED, "non-final resume should report PARKED");
        CHECK(st.ticks == resumes, "one tick should accrue per resume before a park");
        CHECK(resumes <= st.target, "parked more times than the loop should allow");
    }

    /* After completion: every loop iteration ran, the body re-entered past each of the
     * first `target-1` yields plus the final park, finished exactly once, and the status
     * latched COMPLETED. */
    CHECK(st.done == 1, "coroutine never marked done");
    CHECK(st.ticks == st.target, "wrong number of ticks");
    CHECK(st.reentry == st.target, "wrong number of re-entries past yield");
    CHECK(resumes == st.target + 1, "expected target parks plus one finishing resume");
    CHECK(rf_coro_status_get(coro) == RF_CORO_COMPLETED, "final status should be COMPLETED");

    /* Resuming a completed coroutine is a defined no-op. */
    CHECK(rf_coro_resume(coro) == RF_CORO_COMPLETED, "resume after completion must stay COMPLETED");
    CHECK(st.ticks == st.target, "completed coroutine must not run further");

    rf_coro_delete(coro);

    printf("OK: %d resumes, %d ticks, %d re-entries, completed once\n",
           resumes, st.ticks, st.reentry);
    return 0;
}