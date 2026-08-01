/*
 * signal_runtime.c — native backend for RazorForge `SignalCaster` (a condition-variable monitor).
 *
 * A SignalCaster is a self-contained monitor: an internal mutex plus a wait set. A waiter takes the
 * lock, checks its predicate, and `wait`s while it does not hold; `wait` atomically releases the lock,
 * suspends, and re-acquires the lock on wake — the classic condition-variable contract that rules out
 * lost wakeups (the predicate is always tested under the lock). `cast_one` / `cast_all` wake one / all
 * waiters.
 *
 * `wait` is UNCOLORED, reusing the channel/scheduler substrate: inside a scheduler-driven coroutine it
 * registers on the wait set, drops the lock, and parks via rf_sched_park_external (a caster on any
 * thread wakes it with rf_sched_wake); on a plain thread it blocks on the internal condvar. Either way
 * it re-acquires the monitor lock before returning, so callers re-check the predicate in a loop.
 *
 * The handle is refcounted (clone adds a ref, drop releases one) so it can be shared across the agents
 * that synchronize through it; the struct frees when the last handle drops.
 */
#include "../include/razorforge_runtime.h"
#include "rf_sync.h"

#include <stdlib.h>

extern void __rf_throw(const char* error_type, const char* message);

/* A parked coroutine waiter: (scheduler, coroutine) so any thread's cast can wake it. Lives on the
 * parked coroutine's own stack while linked; unlinked before wait returns. */
typedef struct rf_signal_waiter
{
    rf_sched* sched;
    rf_coro* coro;
    struct rf_signal_waiter* next;
} rf_signal_waiter;

struct rf_signal
{
    rf_mutex lock;             /* the monitor mutex (user-facing: lock/unlock guard the predicate) */
    rf_cond cond;              /* thread waiters block here */
    rf_signal_waiter* waiters; /* coroutine waiters (woken via rf_sched_wake) */
    uint32_t refcount;         /* live SignalCaster handles */
};

rf_signal* rf_signal_create(void)
{
    rf_signal* sig = (rf_signal*)calloc(1, sizeof(rf_signal));
    if (sig == NULL) {
        __rf_throw("OutOfMemoryError", "failed to allocate SignalCaster");
        return NULL;
    }
    rf_mutex_init(&sig->lock);
    rf_cond_init(&sig->cond);
    sig->refcount = 1;
    return sig;
}

void rf_signal_add_ref(rf_signal* sig)
{
    if (sig == NULL) return;
    rf_mutex_lock(&sig->lock);
    sig->refcount++;
    rf_mutex_unlock(&sig->lock);
}

/* Drop a handle; free the monitor once the last one goes. */
void rf_signal_drop(rf_signal* sig)
{
    if (sig == NULL) return;
    rf_mutex_lock(&sig->lock);
    uint32_t remaining = sig->refcount > 0 ? --sig->refcount : 0;
    rf_mutex_unlock(&sig->lock);
    if (remaining == 0) {
        rf_mutex_destroy(&sig->lock);
        rf_cond_destroy(&sig->cond);
        free(sig);
    }
}

/* Acquire / release the monitor lock that guards the caller's shared predicate. */
void rf_signal_lock(rf_signal* sig)
{
    if (sig == NULL) return;
    rf_mutex_lock(&sig->lock);
}

void rf_signal_unlock(rf_signal* sig)
{
    if (sig == NULL) return;
    rf_mutex_unlock(&sig->lock);
}

/* Wait for a cast. MUST be called holding the monitor lock. Atomically releases the lock, suspends
 * (coroutine park or thread condvar), and re-acquires the lock before returning. Uncolored. */
void rf_signal_wait(rf_signal* sig)
{
    if (sig == NULL) return;

    rf_sched* sched = rf_sched_current();
    rf_coro* self = rf_coro_current();
    if (sched != NULL && self != NULL) {
        /* Coroutine: register, drop the lock, park. A cast on any thread re-queues us via
         * rf_sched_wake (wake-before-park is not lost — it lands in the ready queue). */
        rf_signal_waiter node = { sched, self, sig->waiters };
        sig->waiters = &node;
        rf_mutex_unlock(&sig->lock);
        /* A cast on any thread wakes us cross-thread (rf_sched_wake): arm/disarm a cross-waker around
         * the park so the run loop does not read this wait as a deadlock. */
        rf_sched_arm_cross_waker(sched);
        rf_sched_park_external();
        rf_sched_disarm_cross_waker(sched);
        rf_mutex_lock(&sig->lock);
        /* Unlink self. */
        rf_signal_waiter** p = &sig->waiters;
        while (*p != NULL) {
            if (*p == &node) { *p = node.next; break; }
            p = &(*p)->next;
        }
    } else {
        /* Plain thread: the condvar atomically releases the lock, waits, and re-acquires it. */
        rf_cond_wait_forever(&sig->cond, &sig->lock);
    }
}

/* Timed wait: like rf_signal_wait but bounded by `timeout_ns`. Returns 1 if woken by a cast (or a
 * spurious wake) before the deadline — re-check your predicate — and 0 if the deadline elapsed first.
 * MUST hold the monitor lock; releases and re-acquires it around the suspend, as wait does. */
uint32_t rf_signal_wait_deadline(rf_signal* sig, uint64_t timeout_ns)
{
    if (sig == NULL) return 0;

    uint64_t deadline = rf_monotonic_now_ns() + timeout_ns;
    rf_sched* sched = rf_sched_current();
    rf_coro* self = rf_coro_current();
    if (sched != NULL && self != NULL) {
        /* Coroutine: park on a deadline timer that a cast can also wake (rf_sched_park_deadline). On
         * resume, the clock tells cast from timeout — woken early means a cast, else the timer fired. */
        rf_signal_waiter node = { sched, self, sig->waiters };
        sig->waiters = &node;
        rf_mutex_unlock(&sig->lock);
        rf_sched_park_deadline(timeout_ns);
        rf_mutex_lock(&sig->lock);
        rf_signal_waiter** p = &sig->waiters;
        while (*p != NULL) {
            if (*p == &node) { *p = node.next; break; }
            p = &(*p)->next;
        }
    } else {
        /* Plain thread: a timed condvar wait. */
        rf_cond_wait_ns(&sig->cond, &sig->lock, timeout_ns);
    }
    return rf_monotonic_now_ns() >= deadline ? 0u : 1u;
}

/* Wake one waiter. Prefer a parked coroutine if any (the most local), else signal one thread. Spurious
 * over-wakes are harmless — every waiter re-checks its predicate under the lock. */
void rf_signal_cast_one(rf_signal* sig)
{
    if (sig == NULL) return;
    rf_mutex_lock(&sig->lock);
    if (sig->waiters != NULL) {
        rf_sched_wake(sig->waiters->sched, sig->waiters->coro);
    } else {
        rf_cond_signal(&sig->cond);
    }
    rf_mutex_unlock(&sig->lock);
}

/* Wake every waiter (coroutines + threads). */
void rf_signal_cast_all(rf_signal* sig)
{
    if (sig == NULL) return;
    rf_mutex_lock(&sig->lock);
    for (rf_signal_waiter* w = sig->waiters; w != NULL; w = w->next) {
        rf_sched_wake(w->sched, w->coro);
    }
    rf_cond_broadcast(&sig->cond);
    rf_mutex_unlock(&sig->lock);
}
