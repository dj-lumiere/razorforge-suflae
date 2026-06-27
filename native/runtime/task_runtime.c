#include "types.h"
#include "../include/razorforge_runtime.h"
#include "rf_sync.h" /* portable rf_mutex for the task↔coro await handshake */

#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <process.h>
#include <windows.h>
#else
#include <errno.h>
#include <pthread.h>
#include <time.h>
#endif

/* Stable identifier for the calling OS thread. The exact value is opaque — only equality across
 * calls on the same thread matters (lock re-entrancy detection). */
uint64_t rf_current_thread_id(void)
{
#ifdef _WIN32
    return (uint64_t)GetCurrentThreadId();
#else
    return (uint64_t)(uintptr_t)pthread_self();
#endif
}

typedef struct rf_task_node
{
    struct rf_task_node* next;
    struct rf_task* task;
} rf_task_node;

typedef struct rf_thread_start_data
{
    struct rf_task* task;
    rf_task_entry_fn entry;
    void* userdata;
} rf_thread_start_data;

typedef struct rf_thread_backend
{
#ifdef _WIN32
    HANDLE handle;
    unsigned thread_id;
    HANDLE completion_event;
#else
    pthread_t thread;
    pthread_mutex_t lock;
    pthread_cond_t completion_cond;
#endif
} rf_thread_backend;

struct rf_task
{
    rf_task_kind kind;
    rf_task_status status;
    rf_task_completion completion;

    rf_Bool cancel_requested;
    rf_Bool result_consumed;

    void* execution_backend;
    void* wait_backend;
    void* dependency_backend;

    rf_task_node* dependents_head;
    rf_task_node* dependents_tail;

    rf_U32 prerequisite_count;
    rf_U32 prerequisites_remaining;
    rf_U64 task_id;

    /* Coroutine awaiting this task (set by rf_task_await_coro, cleared+woken at completion). When
     * a scheduler-driven coroutine retrieves a threaded Task it parks instead of blocking the
     * thread; the worker wakes it here. coro_lock makes register-vs-complete race-free. */
    rf_mutex coro_lock;
    rf_sched* coro_sched;
    rf_coro* coro_waiter;

    /* A scheduler driving a `race!` over a set that includes this task. On completion the worker
     * signals this scheduler's run loop (no specific coroutine — the racing loop re-polls all
     * competitors under the scheduler lock). Set/cleared by rf_task_race_register under coro_lock;
     * read under coro_lock at completion, the same race-free pattern as coro_sched/coro_waiter. */
    rf_sched* race_sched;
};

static rf_U64 rf_next_task_id = 1;

static rf_task_node* rf_task_node_new(rf_task* task)
{
    rf_task_node* node = (rf_task_node*)calloc(1, sizeof(rf_task_node));
    if (node == NULL) return NULL;
    node->task = task;
    return node;
}

static void rf_task_dependent_append(rf_task* task, rf_task* dependent)
{
    rf_task_node* node;

    if (task == NULL || dependent == NULL) return;

    node = rf_task_node_new(dependent);
    if (node == NULL) return;

    if (task->dependents_tail == NULL)
    {
        task->dependents_head = node;
        task->dependents_tail = node;
        return;
    }

    task->dependents_tail->next = node;
    task->dependents_tail = node;
}

static rf_thread_backend* rf_task_thread_backend(rf_task* task)
{
    if (task == NULL || task->kind != RF_TASK_THREADED)
    {
        return NULL;
    }

    return (rf_thread_backend*)task->wait_backend;
}

static rf_Bool rf_task_is_completed(rf_task* task)
{
    return task != NULL && task->status == RF_TASK_COMPLETED &&
           task->completion.kind != RF_TASK_COMPLETION_PENDING;
}

static void rf_task_signal_completion(rf_task* task)
{
    if (task == NULL) return;

    /* Wake a coroutine awaiting this task (if one parked via rf_task_await_coro). Done under
     * coro_lock and BEFORE the thread-backend signal: register-vs-complete is race-free because
     * await_coro re-checks completion under the same lock, and the status store happens-before this
     * locked read (program order). Reads + clears the slot so a redundant signal can't double-wake. */
    rf_mutex_lock(&task->coro_lock);
    rf_sched* waking_sched = task->coro_sched;
    rf_coro* waiter = task->coro_waiter;
    rf_sched* race_sched = task->race_sched;
    task->coro_sched = NULL;
    task->coro_waiter = NULL;
    rf_mutex_unlock(&task->coro_lock);
    if (waking_sched != NULL && waiter != NULL) {
        rf_sched_wake(waking_sched, waiter);
    }
    /* Also wake a `race!` loop driving a set that includes this task: it parks on the scheduler
     * cond with no specific awaiter coroutine, so signal the cond and let it re-poll. Left set
     * (not cleared) so a later spurious signal is harmless; race! clears it when the loop ends. */
    if (race_sched != NULL) {
        rf_sched_signal(race_sched);
    }

    rf_thread_backend* backend = rf_task_thread_backend(task);
    if (backend == NULL) return;

#ifdef _WIN32
    SetEvent(backend->completion_event);
#else
    pthread_mutex_lock(&backend->lock);
    pthread_cond_broadcast(&backend->completion_cond);
    pthread_mutex_unlock(&backend->lock);
#endif
}

/* Register the CURRENT scheduler-driven coroutine as the awaiter of `task`, so the worker thread
 * wakes it via rf_sched_wake when the task completes. Returns 1 if the task is ALREADY complete
 * (the caller should read the result without parking), 0 if registered (the caller should
 * rf_sched_park_external, to be woken on completion). The completion check is under coro_lock, so
 * it cannot lose a wake against a task finishing concurrently. Call only inside a coroutine driven
 * by a scheduler (rf_in_coroutine() != 0); outside one it conservatively reports "complete" (1) so
 * the caller falls back to a blocking wait. */
/* Register (s != NULL) or clear (s == NULL) the scheduler that a `race!` over a set including this
 * task is driving, so the worker can signal that loop on completion. Idempotent; under coro_lock so
 * it cannot race a concurrent completion read. */
void rf_task_race_register(rf_task* task, rf_sched* s)
{
    if (task == NULL) return;
    rf_mutex_lock(&task->coro_lock);
    task->race_sched = s;
    rf_mutex_unlock(&task->coro_lock);
}

rf_U32 rf_task_await_coro(rf_task* task)
{
    if (task == NULL) return 1;

    rf_sched* sched = rf_sched_current();
    rf_coro* self = rf_coro_current();
    if (sched == NULL || self == NULL) {
        return 1; /* not on a scheduler-driven coroutine — caller must block-wait instead */
    }

    rf_mutex_lock(&task->coro_lock);
    if (rf_task_is_completed(task)) {
        rf_mutex_unlock(&task->coro_lock);
        return 1;
    }
    task->coro_sched = sched;
    task->coro_waiter = self;
    rf_mutex_unlock(&task->coro_lock);
    return 0;
}

/* Timed await: like rf_task_await_coro but bounded by `timeout_ns`. Parks the current coroutine on
 * a deadline timer that is ALSO externally wakeable, looping until the task completes or the
 * deadline elapses — without blocking the scheduler thread (siblings run meanwhile). Returns:
 *   1 = task completed (read the result),
 *   0 = deadline elapsed first (the caller should treat this as a timeout),
 *   2 = not on a scheduler-driven coroutine (the caller must fall back to a blocking timed wait).
 * The awaiter slot is cleared on timeout (under coro_lock, with a final completion re-check) so a
 * later completion cannot wake a coroutine that already moved on. */
rf_U32 rf_task_await_coro_deadline(rf_task* task, uint64_t timeout_ns)
{
    if (task == NULL) return 1;

    rf_sched* sched = rf_sched_current();
    rf_coro* self = rf_coro_current();
    if (sched == NULL || self == NULL) {
        return 2; /* not on a scheduler-driven coroutine — caller block-waits with the deadline */
    }

    uint64_t deadline = rf_monotonic_now_ns() + timeout_ns;
    for (;;) {
        /* Register + check completion atomically (same race-freedom as rf_task_await_coro). */
        rf_mutex_lock(&task->coro_lock);
        if (rf_task_is_completed(task)) {
            rf_mutex_unlock(&task->coro_lock);
            return 1;
        }
        task->coro_sched = sched;
        task->coro_waiter = self;
        rf_mutex_unlock(&task->coro_lock);

        uint64_t now = rf_monotonic_now_ns();
        if (now >= deadline) {
            /* Deadline reached. Final completion check + deregister, atomically: if the task just
             * completed, prefer the value (return 1); otherwise clear our awaiter slot so a later
             * completion does not wake us after we have returned. */
            rf_mutex_lock(&task->coro_lock);
            if (rf_task_is_completed(task)) {
                rf_mutex_unlock(&task->coro_lock);
                return 1;
            }
            if (task->coro_waiter == self) {
                task->coro_sched = NULL;
                task->coro_waiter = NULL;
            }
            rf_mutex_unlock(&task->coro_lock);
            return 0;
        }

        /* Park until the timer fires OR the worker wakes us; then re-check both conditions. */
        rf_sched_park_deadline(deadline - now);
    }
}

static rf_thread_backend* rf_thread_backend_create(void)
{
    rf_thread_backend* backend = (rf_thread_backend*)calloc(1, sizeof(rf_thread_backend));
    if (backend == NULL) return NULL;

#ifdef _WIN32
    backend->completion_event = CreateEventA(NULL, TRUE, FALSE, NULL);
    if (backend->completion_event == NULL)
    {
        free(backend);
        return NULL;
    }
#else
    pthread_mutex_init(&backend->lock, NULL);
    pthread_cond_init(&backend->completion_cond, NULL);
#endif

    return backend;
}

static void rf_thread_backend_destroy(rf_thread_backend* backend)
{
    if (backend == NULL) return;

#ifdef _WIN32
    if (backend->handle != NULL)
    {
        CloseHandle(backend->handle);
    }
    if (backend->completion_event != NULL)
    {
        CloseHandle(backend->completion_event);
    }
#else
    pthread_cond_destroy(&backend->completion_cond);
    pthread_mutex_destroy(&backend->lock);
#endif

    free(backend);
}

#ifndef _WIN32
static void rf_compute_abs_timespec(struct timespec* ts, rf_S64 timeout_seconds, rf_U32 timeout_nanoseconds)
{
    clock_gettime(CLOCK_REALTIME, ts);
    ts->tv_sec += (time_t)timeout_seconds;
    ts->tv_nsec += (long)timeout_nanoseconds;
    if (ts->tv_nsec >= 1000000000L)
    {
        ts->tv_sec += 1;
        ts->tv_nsec -= 1000000000L;
    }
}
#endif

#ifdef _WIN32
static unsigned __stdcall rf_threaded_task_main(void* raw)
#else
static void* rf_threaded_task_main(void* raw)
#endif
{
    rf_thread_start_data* start_data = (rf_thread_start_data*)raw;
    if (start_data == NULL)
    {
#ifdef _WIN32
        return 0;
#else
        return NULL;
#endif
    }

    if (start_data->task != NULL)
    {
        start_data->task->status = RF_TASK_RUNNING;
    }

    if (start_data->entry != NULL)
    {
        start_data->entry(start_data->task, start_data->userdata);
    }

    free(start_data);

#ifdef _WIN32
    return 0;
#else
    return NULL;
#endif
}

const char* rf_task_kind_name(rf_task_kind kind)
{
    switch (kind)
    {
        case RF_TASK_SUSPENDED: return "suspended";
        case RF_TASK_THREADED: return "threaded";
        default: return "unknown";
    }
}

const char* rf_task_status_name(rf_task_status status)
{
    switch (status)
    {
        case RF_TASK_NEW: return "new";
        case RF_TASK_READY: return "ready";
        case RF_TASK_RUNNING: return "running";
        case RF_TASK_PARKED: return "parked";
        case RF_TASK_COMPLETED: return "completed";
        default: return "unknown";
    }
}

const char* rf_task_completion_name(rf_task_completion_kind kind)
{
    switch (kind)
    {
        case RF_TASK_COMPLETION_PENDING: return "pending";
        case RF_TASK_COMPLETION_VALUE: return "value";
        case RF_TASK_COMPLETION_ERROR: return "error";
        case RF_TASK_COMPLETION_CANCELLED: return "cancelled";
        case RF_TASK_COMPLETION_TIMEOUT: return "timeout";
        default: return "unknown";
    }
}

rf_task* rf_task_create(rf_task_kind kind)
{
    rf_task* task = (rf_task*)calloc(1, sizeof(rf_task));
    if (task == NULL) return NULL;

    task->kind = kind;
    task->status = RF_TASK_NEW;
    task->completion.kind = RF_TASK_COMPLETION_PENDING;
    task->task_id = rf_next_task_id++;
    rf_mutex_init(&task->coro_lock);

    if (kind == RF_TASK_THREADED)
    {
        rf_thread_backend* backend = rf_thread_backend_create();
        if (backend == NULL)
        {
            free(task);
            return NULL;
        }

        task->wait_backend = backend;
        task->execution_backend = backend;
    }

    return task;
}

void rf_task_destroy(rf_task* task)
{
    rf_task_node* node;
    rf_task_node* next;

    if (task == NULL) return;

    node = task->dependents_head;
    while (node != NULL)
    {
        next = node->next;
        free(node);
        node = next;
    }

    rf_thread_backend_destroy((rf_thread_backend*)task->wait_backend);
    rf_mutex_destroy(&task->coro_lock);
    free(task);
}

rf_U64 rf_task_id(rf_task* task)
{
    if (task == NULL) return 0;
    return task->task_id;
}

rf_task_kind rf_task_kind_get(rf_task* task)
{
    if (task == NULL) return RF_TASK_SUSPENDED;
    return task->kind;
}

rf_task_status rf_task_status_get(rf_task* task)
{
    if (task == NULL) return RF_TASK_COMPLETED;
    return task->status;
}

rf_task_completion_kind rf_task_completion_kind_get(rf_task* task)
{
    if (task == NULL) return RF_TASK_COMPLETION_ERROR;
    return task->completion.kind;
}

void* rf_task_result_payload(rf_task* task)
{
    if (task == NULL) return NULL;
    return task->completion.value_payload;
}

void* rf_task_error_payload(rf_task* task)
{
    if (task == NULL) return NULL;
    return task->completion.error_payload;
}

rf_task_completion_kind rf_task_wait(rf_task* task)
{
    if (task == NULL) return RF_TASK_COMPLETION_ERROR;

    if (task->kind == RF_TASK_THREADED && !rf_task_is_completed(task))
    {
        rf_thread_backend* backend = rf_task_thread_backend(task);
        if (backend != NULL)
        {
#ifdef _WIN32
            WaitForSingleObject(backend->completion_event, INFINITE);
#else
            pthread_mutex_lock(&backend->lock);
            while (!rf_task_is_completed(task))
            {
                pthread_cond_wait(&backend->completion_cond, &backend->lock);
            }
            pthread_mutex_unlock(&backend->lock);
#endif
        }
    }

    return task->completion.kind;
}

rf_task_completion_kind rf_task_wait_within(rf_task* task, rf_S64 timeout_seconds, rf_U32 timeout_nanoseconds)
{
    if (task == NULL) return RF_TASK_COMPLETION_ERROR;

    if (rf_task_is_completed(task))
    {
        return task->completion.kind;
    }

    if (task->kind == RF_TASK_THREADED)
    {
        rf_thread_backend* backend = rf_task_thread_backend(task);
        if (backend != NULL)
        {
#ifdef _WIN32
            unsigned long long timeout_ms = (unsigned long long)timeout_seconds * 1000ULL +
                                            ((unsigned long long)timeout_nanoseconds + 999999ULL) / 1000000ULL;
            DWORD wait_result;

            if (timeout_ms > 0xFFFFFFFFULL)
            {
                timeout_ms = 0xFFFFFFFFULL;
            }

            wait_result = WaitForSingleObject(backend->completion_event, (DWORD)timeout_ms);
            if (wait_result == WAIT_TIMEOUT && !rf_task_is_completed(task))
            {
                return RF_TASK_COMPLETION_TIMEOUT;
            }
#else
            struct timespec ts;
            int wait_result;

            rf_compute_abs_timespec(&ts, timeout_seconds, timeout_nanoseconds);
            pthread_mutex_lock(&backend->lock);
            while (!rf_task_is_completed(task))
            {
                wait_result = pthread_cond_timedwait(&backend->completion_cond, &backend->lock, &ts);
                if (wait_result == ETIMEDOUT && !rf_task_is_completed(task))
                {
                    pthread_mutex_unlock(&backend->lock);
                    return RF_TASK_COMPLETION_TIMEOUT;
                }
            }
            pthread_mutex_unlock(&backend->lock);
#endif
        }
    }

    return task->completion.kind;
}

int rf_task_spawn_threaded(rf_task* task, rf_task_entry_fn entry, void* userdata)
{
    rf_thread_backend* backend;
    rf_thread_start_data* start_data;

    if (task == NULL || entry == NULL || task->kind != RF_TASK_THREADED)
    {
        return 0;
    }

    backend = rf_task_thread_backend(task);
    if (backend == NULL)
    {
        return 0;
    }

    start_data = (rf_thread_start_data*)calloc(1, sizeof(rf_thread_start_data));
    if (start_data == NULL)
    {
        return 0;
    }

    start_data->task = task;
    start_data->entry = entry;
    start_data->userdata = userdata;

    task->status = RF_TASK_READY;

#ifdef _WIN32
    backend->handle = (HANDLE)_beginthreadex(NULL, 0, rf_threaded_task_main, start_data, 0, &backend->thread_id);
    if (backend->handle == NULL)
    {
        free(start_data);
        rf_task_complete_error(task, NULL);
        return 0;
    }
#else
    if (pthread_create(&backend->thread, NULL, rf_threaded_task_main, start_data) != 0)
    {
        free(start_data);
        rf_task_complete_error(task, NULL);
        return 0;
    }
#endif

    return 1;
}

void rf_task_mark_ready(rf_task* task)
{
    if (task == NULL) return;
    task->status = RF_TASK_READY;
}

void rf_task_mark_running(rf_task* task)
{
    if (task == NULL) return;
    task->status = RF_TASK_RUNNING;
}

void rf_task_mark_parked(rf_task* task)
{
    if (task == NULL) return;
    task->status = RF_TASK_PARKED;
}

void rf_task_complete_value(rf_task* task, void* result_payload)
{
    if (task == NULL) return;

    task->status = RF_TASK_COMPLETED;
    task->completion.kind = RF_TASK_COMPLETION_VALUE;
    task->completion.value_payload = result_payload;
    task->completion.error_payload = NULL;
    rf_task_signal_completion(task);
}

void rf_task_complete_error(rf_task* task, void* error_payload)
{
    if (task == NULL) return;

    task->status = RF_TASK_COMPLETED;
    task->completion.kind = RF_TASK_COMPLETION_ERROR;
    task->completion.value_payload = NULL;
    task->completion.error_payload = error_payload;
    rf_task_signal_completion(task);
}

void rf_task_complete_cancelled(rf_task* task)
{
    if (task == NULL) return;

    task->status = RF_TASK_COMPLETED;
    task->completion.kind = RF_TASK_COMPLETION_CANCELLED;
    task->completion.value_payload = NULL;
    task->completion.error_payload = NULL;
    rf_task_signal_completion(task);
}

void rf_task_complete_timeout(rf_task* task)
{
    if (task == NULL) return;

    task->status = RF_TASK_COMPLETED;
    task->completion.kind = RF_TASK_COMPLETION_TIMEOUT;
    task->completion.value_payload = NULL;
    task->completion.error_payload = NULL;
    rf_task_signal_completion(task);
}

void rf_task_request_cancel(rf_task* task)
{
    if (task == NULL) return;
    task->cancel_requested = true;
}

rf_Bool rf_task_is_cancel_requested(rf_task* task)
{
    if (task == NULL) return false;
    return task->cancel_requested;
}

void rf_task_mark_result_consumed(rf_task* task)
{
    if (task == NULL) return;
    task->result_consumed = true;
}

rf_Bool rf_task_is_result_consumed(rf_task* task)
{
    if (task == NULL) return false;
    return task->result_consumed;
}

void rf_task_attach_execution_backend(rf_task* task, void* backend)
{
    if (task == NULL) return;
    task->execution_backend = backend;
}

void* rf_task_execution_backend(rf_task* task)
{
    if (task == NULL) return NULL;
    return task->execution_backend;
}

void rf_task_attach_wait_backend(rf_task* task, void* backend)
{
    if (task == NULL) return;
    task->wait_backend = backend;
}

void* rf_task_wait_backend(rf_task* task)
{
    if (task == NULL) return NULL;
    return task->wait_backend;
}

void rf_task_add_prerequisite(rf_task* task)
{
    if (task == NULL) return;
    task->prerequisite_count += 1;
    task->prerequisites_remaining += 1;
}

void rf_task_add_dependent(rf_task* task, rf_task* dependent)
{
    if (task == NULL || dependent == NULL) return;
    rf_task_dependent_append(task, dependent);
}

rf_U32 rf_task_prerequisite_count(rf_task* task)
{
    if (task == NULL) return 0;
    return task->prerequisite_count;
}

rf_U32 rf_task_prerequisites_remaining(rf_task* task)
{
    if (task == NULL) return 0;
    return task->prerequisites_remaining;
}

rf_Bool rf_task_prerequisite_complete(rf_task* task, rf_Bool success)
{
    if (task == NULL) return false;

    if (!success)
    {
        task->status = RF_TASK_COMPLETED;
        task->completion.kind = RF_TASK_COMPLETION_CANCELLED;
        rf_task_signal_completion(task);
        return false;
    }

    if (task->prerequisites_remaining > 0)
    {
        task->prerequisites_remaining -= 1;
    }

    if (task->prerequisites_remaining == 0)
    {
        if (task->status == RF_TASK_NEW)
        {
            task->status = RF_TASK_READY;
        }
        return true;
    }

    return false;
}
