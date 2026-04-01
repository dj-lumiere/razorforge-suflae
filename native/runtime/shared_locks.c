// shared_locks.c - Placeholder native locks for Shared<T, Policy>

#include "types.h"

#ifdef _WIN32
#include <windows.h>
#else
#include <pthread.h>
#endif

typedef struct
{
    void* data;
#ifdef _WIN32
    CRITICAL_SECTION mutex;
    SRWLOCK rwlock;
#else
    pthread_mutex_t mutex;
    pthread_rwlock_t rwlock;
#endif
    rf_U32 ref_count;
} rf_Shared;

void* razorforge_mutex_lock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    EnterCriticalSection(&shared->mutex);
#else
    pthread_mutex_lock(&shared->mutex);
#endif
    return shared->data;
}

void razorforge_mutex_unlock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    LeaveCriticalSection(&shared->mutex);
#else
    pthread_mutex_unlock(&shared->mutex);
#endif
}

void* razorforge_rwlock_read_lock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    AcquireSRWLockShared(&shared->rwlock);
#else
    pthread_rwlock_rdlock(&shared->rwlock);
#endif
    return shared->data;
}

void razorforge_rwlock_read_unlock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    ReleaseSRWLockShared(&shared->rwlock);
#else
    pthread_rwlock_unlock(&shared->rwlock);
#endif
}

void* razorforge_rwlock_write_lock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    AcquireSRWLockExclusive(&shared->rwlock);
#else
    pthread_rwlock_wrlock(&shared->rwlock);
#endif
    return shared->data;
}

void razorforge_rwlock_write_unlock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    ReleaseSRWLockExclusive(&shared->rwlock);
#else
    pthread_rwlock_unlock(&shared->rwlock);
#endif
}
