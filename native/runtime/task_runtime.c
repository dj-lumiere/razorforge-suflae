#include "types.h"
#include "../include/razorforge_runtime.h"

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
