#ifndef RAZORFORGE_RUNTIME_H
#define RAZORFORGE_RUNTIME_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct rf_context_runtime rf_context_runtime;
typedef struct rf_async_runtime rf_async_runtime;
typedef struct rf_task rf_task;

typedef void (*rf_context_entry_fn)(void* userdata);
typedef void (*rf_task_entry_fn)(rf_task* task, void* userdata);

typedef enum rf_task_kind {
    RF_TASK_SUSPENDED = 0,
    RF_TASK_THREADED = 1
} rf_task_kind;

typedef enum rf_task_status {
    RF_TASK_NEW = 0,
    RF_TASK_READY = 1,
    RF_TASK_RUNNING = 2,
    RF_TASK_PARKED = 3,
    RF_TASK_COMPLETED = 4
} rf_task_status;

typedef enum rf_task_completion_kind {
    RF_TASK_COMPLETION_PENDING = 0,
    RF_TASK_COMPLETION_VALUE = 1,
    RF_TASK_COMPLETION_ERROR = 2,
    RF_TASK_COMPLETION_CANCELLED = 3,
    RF_TASK_COMPLETION_TIMEOUT = 4
} rf_task_completion_kind;

typedef struct rf_task_completion {
    rf_task_completion_kind kind;
    void* value_payload;
    void* error_payload;
} rf_task_completion;

typedef enum rf_runtime_backend_state {
    RF_RUNTIME_BACKEND_UNAVAILABLE = 0,
    RF_RUNTIME_BACKEND_AVAILABLE = 1
} rf_runtime_backend_state;

const char* rf_context_backend_name(void);
rf_runtime_backend_state rf_context_backend_state(void);

const char* rf_async_backend_name(void);
rf_runtime_backend_state rf_async_backend_state(void);

rf_context_runtime* rf_context_runtime_create(void);
void rf_context_runtime_destroy(rf_context_runtime* runtime);
int rf_context_runtime_spawn(rf_context_runtime* runtime, rf_context_entry_fn entry, void* userdata, size_t stack_size);

rf_async_runtime* rf_async_runtime_create(void);
void rf_async_runtime_destroy(rf_async_runtime* runtime);
int rf_async_runtime_run_once(rf_async_runtime* runtime);
int rf_async_runtime_run_default(rf_async_runtime* runtime);
void rf_async_runtime_stop(rf_async_runtime* runtime);

const char* rf_task_kind_name(rf_task_kind kind);
const char* rf_task_status_name(rf_task_status status);
const char* rf_task_completion_name(rf_task_completion_kind kind);

rf_task* rf_task_create(rf_task_kind kind);
void rf_task_destroy(rf_task* task);

uint64_t rf_task_id(rf_task* task);
rf_task_kind rf_task_kind_get(rf_task* task);
rf_task_status rf_task_status_get(rf_task* task);
rf_task_completion_kind rf_task_completion_kind_get(rf_task* task);
void* rf_task_result_payload(rf_task* task);
void* rf_task_error_payload(rf_task* task);
rf_task_completion_kind rf_task_wait(rf_task* task);
rf_task_completion_kind rf_task_wait_within(rf_task* task, int64_t timeout_seconds, uint32_t timeout_nanoseconds);
int rf_task_spawn_threaded(rf_task* task, rf_task_entry_fn entry, void* userdata);

void rf_task_mark_ready(rf_task* task);
void rf_task_mark_running(rf_task* task);
void rf_task_mark_parked(rf_task* task);

void rf_task_complete_value(rf_task* task, void* result_payload);
void rf_task_complete_error(rf_task* task, void* error_payload);
void rf_task_complete_cancelled(rf_task* task);
void rf_task_complete_timeout(rf_task* task);

void rf_task_request_cancel(rf_task* task);
bool rf_task_is_cancel_requested(rf_task* task);

void rf_task_mark_result_consumed(rf_task* task);
bool rf_task_is_result_consumed(rf_task* task);

void rf_task_attach_execution_backend(rf_task* task, void* backend);
void* rf_task_execution_backend(rf_task* task);
void rf_task_attach_wait_backend(rf_task* task, void* backend);
void* rf_task_wait_backend(rf_task* task);

void rf_task_add_prerequisite(rf_task* task);
void rf_task_add_dependent(rf_task* task, rf_task* dependent);
uint32_t rf_task_prerequisite_count(rf_task* task);
uint32_t rf_task_prerequisites_remaining(rf_task* task);
bool rf_task_prerequisite_complete(rf_task* task, bool success);

void rf_waitfor_duration(int64_t duration_seconds, uint32_t duration_nanoseconds);

#ifdef __cplusplus
}
#endif

#endif
