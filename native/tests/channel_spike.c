/*
 * channel_spike.c — native channel harness (no RF front-end).
 *
 * Proves the v0.3.0 `rf_channel` backend against the two contracts that de-risk the design:
 *
 *   Scenario A — cross-thread feed/next + backpressure (plain OS threads, condvar path):
 *     A producer thread feeds 1..N into a capacity-2 channel; a deliberately slower consumer thread
 *     drains it. Asserts every value arrives exactly once, IN ORDER, and the buffered count never
 *     exceeds the capacity (backpressure held the producer back). The last Feeder drop auto-closes,
 *     so the consumer drains then sees NULL and stops.
 *
 *   Scenario B — coroutine park + cross-thread wake (scheduler path):
 *     A consumer COROUTINE calls rf_channel_next on an empty channel and PARKS (rf_sched_park_external
 *     via the channel's coroutine wait path), without blocking the scheduler thread — a sibling
 *     coroutine keeps ticking. A worker THREAD then feeds one item, whose rf_sched_wake re-queues the
 *     parked consumer. Asserts the consumer received the value and the sibling progressed while it
 *     was parked.
 *
 * Build (Windows x64, clang):
 *   clang -std=c23 -I native/include -I native/libco -DHAVE_LIBCO \
 *       native/tests/channel_spike.c native/runtime/channel_runtime.c native/runtime/coro_runtime.c \
 *       native/runtime/concurrency_context.c native/libco/libco.c -o build/channel_spike.exe
 * (POSIX: append -lpthread.)
 *
 * Exit code 0 = all assertions passed.
 */
#include "razorforge_runtime.h"

#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>

/* channel_runtime.c raises this on allocation failure; the spike never triggers it, but the symbol
 * must resolve. */
void __rf_throw(const char* error_type, const char* message)
{
    fprintf(stderr, "__rf_throw: %s: %s\n", error_type, message);
    exit(2);
}

/* ---- portable OS-thread spawn + sleep (spike-local) ------------------------------------------- */
#ifdef _WIN32
#include <windows.h>
typedef HANDLE xthread_t;
static void sleep_ms(unsigned ms) { Sleep(ms); }
typedef void (*body_fn)(void*);
typedef struct { body_fn fn; void* arg; } thunk;
static DWORD WINAPI win_trampoline(LPVOID arg) { thunk* t = (thunk*)arg; t->fn(t->arg); return 0; }
static xthread_t start_thread(body_fn fn, void* arg)
{
    thunk* t = (thunk*)malloc(sizeof(thunk));
    t->fn = fn; t->arg = arg;
    return CreateThread(NULL, 0, win_trampoline, t, 0, NULL);
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
typedef void (*body_fn)(void*);
typedef struct { body_fn fn; void* arg; } thunk;
static void* posix_trampoline(void* arg) { thunk* t = (thunk*)arg; t->fn(t->arg); free(t); return NULL; }
static xthread_t start_thread(body_fn fn, void* arg)
{
    thunk* t = (thunk*)malloc(sizeof(thunk));
    t->fn = fn; t->arg = arg;
    pthread_t th; pthread_create(&th, NULL, posix_trampoline, t); return th;
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

/* Encode a 1-based integer as a payload pointer (never NULL, which is the closed-and-drained
 * sentinel). */
static void* enc(int64_t v) { return (void*)(intptr_t)v; }
static int64_t dec(void* p) { return (int64_t)(intptr_t)p; }

/* ===== Scenario A: thread<->thread + backpressure ============================================== */

#define A_N 20
#define A_CAP 2

typedef struct {
    rf_channel* chan;
    int feed_ok;          /* count of feeds that returned 1 */
} a_producer_state;

static void a_producer(void* arg)
{
    a_producer_state* st = (a_producer_state*)arg;
    for (int64_t i = 1; i <= A_N; i++) {
        if (rf_channel_feed(st->chan, enc(i))) {
            st->feed_ok++;
        }
    }
    rf_channel_drop_feeder(st->chan); /* last feeder -> auto-close */
}

typedef struct {
    rf_channel* chan;
    int recv_count;
    int in_order;         /* 1 if every value arrived as the expected next integer */
    uint64_t max_buffered; /* high-water mark of buffered count (must stay <= capacity) */
} a_consumer_state;

static void a_consumer(void* arg)
{
    a_consumer_state* st = (a_consumer_state*)arg;
    int64_t expected = 1;
    st->in_order = 1;
    for (;;) {
        uint64_t buffered = rf_channel_count(st->chan);
        if (buffered > st->max_buffered) st->max_buffered = buffered;
        void* p = rf_channel_next(st->chan);
        if (p == NULL) break; /* closed and drained */
        if (dec(p) != expected) st->in_order = 0;
        expected++;
        st->recv_count++;
        sleep_ms(2); /* slower than the producer -> forces the buffer to fill = backpressure */
    }
    rf_channel_drop_consumer(st->chan); /* frees the channel once the producer also dropped */
}

static int scenario_a(void)
{
    rf_channel* chan = rf_channel_create(A_CAP);
    CHECK(chan != NULL, "A: channel create");

    a_producer_state ps = { chan, 0 };
    a_consumer_state cs = { chan, 0, 0, 0 };

    xthread_t tp = start_thread(a_producer, &ps);
    xthread_t tc = start_thread(a_consumer, &cs);
    join_thread(tp);
    join_thread(tc);

    printf("A: received %d/%d, in_order=%d, feed_ok=%d, max_buffered=%llu (cap=%d)\n",
           cs.recv_count, A_N, cs.in_order, ps.feed_ok, (unsigned long long)cs.max_buffered, A_CAP);

    CHECK(cs.recv_count == A_N, "A: every value received exactly once");
    CHECK(cs.in_order == 1, "A: values arrived in order");
    CHECK(ps.feed_ok == A_N, "A: every feed succeeded");
    CHECK(cs.max_buffered <= A_CAP, "A: backpressure — buffer never exceeded capacity");
    return 0;
}

/* ===== Scenario B: coroutine parks on empty, woken cross-thread by a feeder ==================== */

typedef struct {
    rf_channel* chan;
    int64_t got;
} b_state;

static int g_b_sibling_ticks;
static int g_b_ticks_at_recv;

/* Worker thread: after a delay, feed one item — whose wake re-queues the parked consumer coro. */
static void b_feeder_thread(void* arg)
{
    b_state* st = (b_state*)arg;
    sleep_ms(40);
    (void)rf_channel_feed(st->chan, enc(99));
}

static xthread_t g_b_worker;

static void b_consumer_body(void* ud)
{
    b_state* st = (b_state*)ud;
    g_b_worker = start_thread(b_feeder_thread, st); /* arm the feeder BEFORE parking */
    void* p = rf_channel_next(st->chan);            /* empty -> parks; woken by the feeder thread */
    st->got = dec(p);
    g_b_ticks_at_recv = g_b_sibling_ticks;
}

static void b_sibling_body(void* ud)
{
    (void)ud;
    for (int i = 0; i < 5; i++) {
        rf_sched_park_timer(10ull * 1000000ull);
        g_b_sibling_ticks++;
    }
}

static int scenario_b(void)
{
    rf_channel* chan = rf_channel_create(1);
    CHECK(chan != NULL, "B: channel create");
    b_state st = { chan, -1 };

    rf_sched* s = rf_sched_create();
    CHECK(s != NULL, "B: sched create");

    rf_coro* c_cons = rf_coro_create(b_consumer_body, &st, 0);
    rf_coro* c_sib = rf_coro_create(b_sibling_body, NULL, 0);
    CHECK(c_cons && c_sib, "B: coro create");

    rf_sched_spawn(s, c_cons);
    rf_sched_spawn(s, c_sib);
    rf_sched_run(s); /* blocks on the cond while the consumer is parked; sibling keeps ticking */

    join_thread(g_b_worker);

    printf("B: consumer got %lld; sibling ticks=%d; ticks when received=%d\n",
           (long long)st.got, g_b_sibling_ticks, g_b_ticks_at_recv);

    CHECK(st.got == 99, "B: consumer received the fed value via cross-thread wake");
    CHECK(g_b_ticks_at_recv >= 1, "B: sibling progressed while the consumer was parked");

    rf_channel_drop_feeder(chan);
    rf_channel_drop_consumer(chan);
    rf_sched_destroy(s);
    return 0;
}

int main(void)
{
    printf("coro backend: %s\n", rf_context_backend_name());
    if (scenario_a() != 0) return 1;
    if (scenario_b() != 0) return 1;
    printf("ALL CHANNEL SPIKE ASSERTIONS PASSED\n");
    return 0;
}
