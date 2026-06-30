/*
 * rf_sync.h — minimal portable mutex + condition variable (runtime-internal).
 *
 * Static-inline wrappers over the platform primitives (Win32 CRITICAL_SECTION / CONDITION_VARIABLE
 * vs POSIX pthread) so multiple runtime translation units (the coroutine scheduler, the task↔coro
 * bridge) share one race-free locking layer instead of each rolling its own. The Win32 timed wait
 * takes a relative ms timeout; POSIX needs an absolute CLOCK_REALTIME deadline — both are hidden
 * behind rf_cond_wait_ns. Included only by .c files in native/runtime; not a public API.
 */
#ifndef RF_SYNC_H
#define RF_SYNC_H

#include <stdint.h>

#ifdef _WIN32
#include <windows.h>
typedef CRITICAL_SECTION rf_mutex;
typedef CONDITION_VARIABLE rf_cond;
#else
#include <pthread.h>
#include <time.h>
typedef pthread_mutex_t rf_mutex;
typedef pthread_cond_t rf_cond;
#endif

static inline void rf_mutex_init(rf_mutex* m)
{
#ifdef _WIN32
    InitializeCriticalSection(m);
#else
    pthread_mutex_init(m, NULL);
#endif
}
static inline void rf_mutex_destroy(rf_mutex* m)
{
#ifdef _WIN32
    DeleteCriticalSection(m);
#else
    pthread_mutex_destroy(m);
#endif
}
static inline void rf_mutex_lock(rf_mutex* m)
{
#ifdef _WIN32
    EnterCriticalSection(m);
#else
    pthread_mutex_lock(m);
#endif
}
static inline void rf_mutex_unlock(rf_mutex* m)
{
#ifdef _WIN32
    LeaveCriticalSection(m);
#else
    pthread_mutex_unlock(m);
#endif
}
static inline void rf_cond_init(rf_cond* c)
{
#ifdef _WIN32
    InitializeConditionVariable(c);
#else
    pthread_cond_init(c, NULL);
#endif
}
static inline void rf_cond_destroy(rf_cond* c)
{
#ifndef _WIN32
    pthread_cond_destroy(c);
#else
    (void)c;
#endif
}
static inline void rf_cond_signal(rf_cond* c)
{
#ifdef _WIN32
    WakeConditionVariable(c);
#else
    pthread_cond_signal(c);
#endif
}
/* Wake ALL waiters — needed when a single state change can satisfy several blocked threads (e.g. a
 * channel close releasing every blocked producer and consumer at once). */
static inline void rf_cond_broadcast(rf_cond* c)
{
#ifdef _WIN32
    WakeAllConditionVariable(c);
#else
    pthread_cond_broadcast(c);
#endif
}
/* Wait on the cond with the mutex held; returns after a signal or `timeout_ns` elapses. */
static inline void rf_cond_wait_ns(rf_cond* c, rf_mutex* m, uint64_t timeout_ns)
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
static inline void rf_cond_wait_forever(rf_cond* c, rf_mutex* m)
{
#ifdef _WIN32
    SleepConditionVariableCS(c, m, INFINITE);
#else
    pthread_cond_wait(c, m);
#endif
}

#endif /* RF_SYNC_H */
