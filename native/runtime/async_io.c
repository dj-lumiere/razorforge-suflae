#include "types.h"
#include "../include/razorforge_runtime.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

/* Runtime error + stack trace + exit(1) (stacktrace.c). */
extern void __rf_throw(const char* error_type, const char* message);

struct rf_async_runtime
{
    rf_Bool should_stop;
};

const char* rf_async_backend_name(void)
{
#ifdef HAVE_LIBUV
    return "libuv";
#else
    return "none";
#endif
}

rf_runtime_backend_state rf_async_backend_state(void)
{
#ifdef HAVE_LIBUV
    return RF_RUNTIME_BACKEND_AVAILABLE;
#else
    return RF_RUNTIME_BACKEND_UNAVAILABLE;
#endif
}

rf_async_runtime* rf_async_runtime_create(void)
{
    rf_async_runtime* runtime = (rf_async_runtime*)calloc(1, sizeof(rf_async_runtime));
    if (runtime == NULL) {
        __rf_throw("OutOfMemoryError", "Failed to allocate async runtime");
        return NULL; /* unreachable */
    }
    return runtime;
}

void rf_async_runtime_destroy(rf_async_runtime* runtime)
{
    free(runtime);
}

int rf_async_runtime_run_once(rf_async_runtime* runtime)
{
    if (runtime == NULL) return 0;
    return runtime->should_stop ? 0 : 1;
}

int rf_async_runtime_run_default(rf_async_runtime* runtime)
{
    if (runtime == NULL) return 0;
    while (!runtime->should_stop)
    {
        if (!rf_async_runtime_run_once(runtime))
        {
            break;
        }
    }
    return 1;
}

void rf_async_runtime_stop(rf_async_runtime* runtime)
{
    if (runtime == NULL) return;
    runtime->should_stop = true;
}

/* ------------------------------------------------------------------------------------------------
 * Coroutine I/O parking (Approach A: libuv loop on its own thread).
 *
 * A dedicated OS thread runs a libuv event loop. A scheduler-driven coroutine that wants to do
 * blocking I/O submits a request (cross-thread, via uv_async_send), parks itself off the scheduler
 * (rf_sched_park_external), and is woken by the uv loop's after-work callback through the SAME
 * cross-thread wake bridge the threaded-task await already uses (rf_sched_wake). The actual blocking
 * work runs on libuv's threadpool (uv_queue_work) so the loop thread never blocks and many
 * coroutines can have I/O in flight concurrently. No change to the scheduler core.
 * ------------------------------------------------------------------------------------------------ */
#ifdef HAVE_LIBUV
#include <uv.h>

/* Exported by coro_runtime.c — the scheduler / coroutine accessors + the cross-thread wake. */
extern rf_sched* rf_sched_current(void);
extern rf_coro*  rf_coro_current(void);
extern void      rf_sched_park_external(void);
extern void      rf_sched_wake(rf_sched* sched, rf_coro* coro);

typedef enum rf_io_kind {
    RF_IO_READ_FILE = 0,
    RF_IO_WRITE_FILE = 1
} rf_io_kind;

typedef struct rf_io_req {
    uv_work_t work;            /* threadpool work handle (work.data points back here)           */
    rf_io_kind kind;
    rf_sched* sched;           /* scheduler driving the awaiting coroutine (NULL = run inline)  */
    rf_coro* coro;             /* the parked coroutine to wake on completion                    */
    struct rf_io_req* qnext;   /* submission-queue link (drained on the loop thread)            */

    char* path;                /* malloc'd NUL-terminated copy of the path                      */
    char* data;                /* read: malloc'd file contents (caller frees). write: malloc'd  */
                               /*       copy of the payload (freed by rf_io_write_file_all).     */
    int64_t data_len;          /* write: number of payload bytes in `data`                      */
    int64_t result;            /* read: bytes read. write: bytes written. < 0 on error          */
    int done;                  /* set by the loop thread before the wake; read after resume     */
} rf_io_req;

typedef struct rf_io_loop {
    uv_loop_t loop;
    uv_thread_t thread;
    uv_async_t submit_async;   /* coro thread -> loop thread: drain the submission queue         */
    uv_mutex_t qlock;          /* guards `queue`                                                 */
    rf_io_req* queue;          /* submission stack (LIFO; order across concurrent I/O is irrelevant) */
} rf_io_loop;

static uv_once_t g_io_once = UV_ONCE_INIT;
static rf_io_loop g_io;
static int g_io_ok = 0;

/* Per-result length, mirroring rf_get_result_len for the sync file API. Thread-local because the
 * value is set on the scheduler thread immediately before rf_io_read_file_all returns and read by
 * RF immediately after — no other coroutine on that thread runs in between. */
static _Thread_local rf_address g_io_last_len = 0;

rf_address rf_io_get_result_len(void)
{
    return g_io_last_len;
}

/* Threadpool thread: do the actual blocking work. Touches only this request. */
static void rf_io_work_cb(uv_work_t* w)
{
    rf_io_req* req = (rf_io_req*)w->data;
    if (req->kind == RF_IO_READ_FILE) {
        FILE* f = fopen(req->path, "rb");
        if (f == NULL) { req->result = -1; return; }
        if (fseek(f, 0, SEEK_END) != 0) { fclose(f); req->result = -1; return; }
        long sz = ftell(f);
        if (sz < 0) { fclose(f); req->result = -1; return; }
        rewind(f);
        char* buf = (char*)malloc((size_t)sz + 1);
        if (buf == NULL) { fclose(f); req->result = -1; return; }
        size_t n = fread(buf, 1, (size_t)sz, f);
        fclose(f);
        buf[n] = '\0';
        req->data = buf;
        req->result = (int64_t)n;
    }
    else if (req->kind == RF_IO_WRITE_FILE) {
        FILE* f = fopen(req->path, "wb");
        if (f == NULL) { req->result = -1; return; }
        size_t n = (req->data_len > 0)
                       ? fwrite(req->data, 1, (size_t)req->data_len, f)
                       : 0;
        int flush_ok = (fflush(f) == 0);
        fclose(f);
        req->result = (flush_ok && n == (size_t)req->data_len) ? (int64_t)n : -1;
    }
}

/* Loop thread: work finished. Mark done and wake the parked coroutine (cross-thread-safe). */
static void rf_io_after_cb(uv_work_t* w, int status)
{
    (void)status;
    rf_io_req* req = (rf_io_req*)w->data;
    req->done = 1;
    if (req->sched != NULL && req->coro != NULL) {
        rf_sched_wake(req->sched, req->coro);
    }
}

/* Loop thread: drain the submission queue and start each request on the threadpool. uv_queue_work
 * must run on the loop thread, so submission hops here via uv_async_send. */
static void rf_io_on_submit(uv_async_t* h)
{
    rf_io_loop* io = (rf_io_loop*)h->data;
    uv_mutex_lock(&io->qlock);
    rf_io_req* list = io->queue;
    io->queue = NULL;
    uv_mutex_unlock(&io->qlock);

    while (list != NULL) {
        rf_io_req* req = list;
        list = req->qnext;
        req->work.data = req;
        uv_queue_work(&io->loop, &req->work, rf_io_work_cb, rf_io_after_cb);
    }
}

static void rf_io_thread_main(void* arg)
{
    rf_io_loop* io = (rf_io_loop*)arg;
    uv_run(&io->loop, UV_RUN_DEFAULT);
}

static void rf_io_init_once(void)
{
    rf_io_loop* io = &g_io;
    if (uv_loop_init(&io->loop) != 0) { return; }
    uv_mutex_init(&io->qlock);
    io->queue = NULL;
    if (uv_async_init(&io->loop, &io->submit_async, rf_io_on_submit) != 0) { return; }
    io->submit_async.data = io;
    if (uv_thread_create(&io->thread, rf_io_thread_main, io) != 0) { return; }
    g_io_ok = 1;
}

/* Eagerly initialise the I/O loop on the MAIN thread/stack at process startup (called from
 * rf_runtime_init). Lazy init from inside a coroutine would run uv_loop_init/uv_thread_create on the
 * demand-paged green stack, which the Windows CRT/Win32 deep-frame paths do not tolerate. Doing it
 * once up front keeps every later coroutine call to pure submit+park (no green-stack libuv work). */
void rf_io_runtime_init(void)
{
    uv_once(&g_io_once, rf_io_init_once);
}

/* Submit a request from any thread: enqueue + wake the loop thread to start it. */
static void rf_io_submit(rf_io_req* req)
{
    rf_io_loop* io = &g_io;
    uv_mutex_lock(&io->qlock);
    req->qnext = io->queue;
    io->queue = req;
    uv_mutex_unlock(&io->qlock);
    uv_async_send(&io->submit_async);
}

/* Run a prepared request to completion: inside a scheduler-driven coroutine, submit it to the I/O
 * loop and park the coroutine (siblings keep running) until the threadpool finishes; outside one, run
 * the blocking work inline. Shared by read and write. */
static void rf_io_run(rf_io_req* req)
{
    rf_sched* sched = g_io_ok ? rf_sched_current() : NULL;
    rf_coro* self = g_io_ok ? rf_coro_current() : NULL;

    if (sched != NULL && self != NULL) {
        req->sched = sched;
        req->coro = self;
        rf_io_submit(req);
        /* Park EXACTLY ONCE to pair with the exactly-once wake from rf_io_after_cb. Do NOT loop on
         * req->done: the work can complete (and wake us) before we park, in which case rf_sched_wake
         * already queued us to ready and a single rf_sched_park_external switch-out is resumed from
         * there. Looping would skip the park when done is already set, leaving us spuriously linked
         * in the ready queue -> use-after-free once the coroutine completes and is freed. The wake
         * sets req->done before signalling, so after the resume req->done is reliably 1. */
        rf_sched_park_external();
    } else {
        /* Not on a scheduler-driven coroutine (or the loop failed to start): run inline. */
        req->work.data = req;
        rf_io_work_cb(&req->work);
    }
}

/* Allocate a request with a NUL-terminated copy of `path`. Throws (does not return) on OOM. */
static rf_io_req* rf_io_req_new(rf_io_kind kind, const char* path, rf_S32 path_len)
{
    rf_io_req* req = (rf_io_req*)calloc(1, sizeof(rf_io_req));
    if (req == NULL) {
        __rf_throw("OutOfMemoryError", "Failed to allocate async I/O request");
        return NULL; /* unreachable */
    }
    req->kind = kind;
    req->result = -1;

    size_t plen = (path_len < 0) ? strlen(path) : (size_t)path_len;
    req->path = (char*)malloc(plen + 1);
    if (req->path == NULL) {
        free(req);
        __rf_throw("OutOfMemoryError", "Failed to allocate async I/O path");
        return NULL; /* unreachable */
    }
    memcpy(req->path, path, plen);
    req->path[plen] = '\0';
    return req;
}

/* Read an entire file asynchronously. Inside a scheduler-driven coroutine this parks the coroutine
 * (siblings keep running) while a threadpool thread does the blocking read; outside one it falls
 * back to running the read inline. Returns a malloc'd, NUL-terminated buffer (caller frees) and sets
 * the result length readable via rf_io_get_result_len; returns NULL on error (length 0). */
char* rf_io_read_file_all(const char* path, rf_S32 path_len)
{
    uv_once(&g_io_once, rf_io_init_once); /* normally already done by rf_io_runtime_init */

    rf_io_req* req = rf_io_req_new(RF_IO_READ_FILE, path, path_len);
    rf_io_run(req);

    char* data = req->data;
    int64_t result = req->result;
    free(req->path);
    free(req);

    if (result < 0) {
        g_io_last_len = 0;
        return NULL;
    }
    g_io_last_len = (rf_address)result;
    return data;
}

/* Write `data_len` bytes from `data` to `path` (truncating), asynchronously. Inside a scheduler-driven
 * coroutine this parks the coroutine while a threadpool thread does the blocking write; outside one it
 * runs inline. The payload is copied into the request so the caller's buffer need not outlive the call.
 * Returns the number of bytes written, or -1 on error. */
int64_t rf_io_write_file_all(const char* path, rf_S32 path_len, const char* data, int64_t data_len)
{
    uv_once(&g_io_once, rf_io_init_once); /* normally already done by rf_io_runtime_init */

    rf_io_req* req = rf_io_req_new(RF_IO_WRITE_FILE, path, path_len);
    if (data_len < 0) {
        data_len = 0;
    }
    req->data_len = data_len;
    if (data_len > 0) {
        req->data = (char*)malloc((size_t)data_len);
        if (req->data == NULL) {
            free(req->path);
            free(req);
            __rf_throw("OutOfMemoryError", "Failed to allocate async I/O write buffer");
            return -1; /* unreachable */
        }
        memcpy(req->data, data, (size_t)data_len);
    }

    rf_io_run(req);

    int64_t result = req->result;
    free(req->data);
    free(req->path);
    free(req);
    return result;
}

#else /* !HAVE_LIBUV — synchronous fallback so the surface exists without the async backend. */

void rf_io_runtime_init(void) {}

static _Thread_local rf_address g_io_last_len = 0;

rf_address rf_io_get_result_len(void)
{
    return g_io_last_len;
}

char* rf_io_read_file_all(const char* path, rf_S32 path_len)
{
    (void)path_len;
    FILE* f = fopen(path, "rb");
    if (f == NULL) { g_io_last_len = 0; return NULL; }
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); g_io_last_len = 0; return NULL; }
    long sz = ftell(f);
    if (sz < 0) { fclose(f); g_io_last_len = 0; return NULL; }
    rewind(f);
    char* buf = (char*)malloc((size_t)sz + 1);
    if (buf == NULL) { fclose(f); g_io_last_len = 0; return NULL; }
    size_t n = fread(buf, 1, (size_t)sz, f);
    fclose(f);
    buf[n] = '\0';
    g_io_last_len = (rf_address)n;
    return buf;
}

int64_t rf_io_write_file_all(const char* path, rf_S32 path_len, const char* data, int64_t data_len)
{
    (void)path_len;
    if (data_len < 0) { data_len = 0; }
    FILE* f = fopen(path, "wb");
    if (f == NULL) { return -1; }
    size_t n = (data_len > 0) ? fwrite(data, 1, (size_t)data_len, f) : 0;
    int flush_ok = (fflush(f) == 0);
    fclose(f);
    return (flush_ok && n == (size_t)data_len) ? (int64_t)n : -1;
}

#endif /* HAVE_LIBUV */