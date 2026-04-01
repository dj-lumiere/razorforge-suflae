#include "types.h"
#include "../include/razorforge_runtime.h"

#include <stdlib.h>
#include <string.h>

typedef struct rf_task_node
{
    struct rf_task_node* next;
    struct rf_task* task;
} rf_task_node;

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
    return task->completion.kind;
}

rf_task_completion_kind rf_task_wait_within(rf_task* task, rf_S64 timeout_seconds, rf_U32 timeout_nanoseconds)
{
    (void)timeout_seconds;
    (void)timeout_nanoseconds;

    if (task == NULL) return RF_TASK_COMPLETION_ERROR;
    return task->completion.kind;
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
}

void rf_task_complete_error(rf_task* task, void* error_payload)
{
    if (task == NULL) return;

    task->status = RF_TASK_COMPLETED;
    task->completion.kind = RF_TASK_COMPLETION_ERROR;
    task->completion.value_payload = NULL;
    task->completion.error_payload = error_payload;
}

void rf_task_complete_cancelled(rf_task* task)
{
    if (task == NULL) return;

    task->status = RF_TASK_COMPLETED;
    task->completion.kind = RF_TASK_COMPLETION_CANCELLED;
    task->completion.value_payload = NULL;
    task->completion.error_payload = NULL;
}

void rf_task_complete_timeout(rf_task* task)
{
    if (task == NULL) return;

    task->status = RF_TASK_COMPLETED;
    task->completion.kind = RF_TASK_COMPLETION_TIMEOUT;
    task->completion.value_payload = NULL;
    task->completion.error_payload = NULL;
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
