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
extern void      rf_sched_arm_cross_waker(rf_sched* sched);
extern void      rf_sched_disarm_cross_waker(rf_sched* sched);

typedef enum rf_io_kind {
    RF_IO_READ_FILE = 0,
    RF_IO_WRITE_FILE = 1,
    RF_IO_RUN_PROCESS = 2
} rf_io_kind;

struct rf_proc_state; /* defined below; non-NULL only for RF_IO_RUN_PROCESS requests */

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
    struct rf_proc_state* proc; /* RF_IO_RUN_PROCESS: uv_spawn handles + captured output        */
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

/* ---- Subprocess (uv_spawn): run a command, capture stdout/stderr, get exit code + signal -------
 * Runs entirely on the loop thread: uv_spawn the command (via the platform shell), redirect the
 * child's stdout/stderr into pipes, accumulate both streams in read callbacks, and on the exit
 * callback record exit_status + term_signal. The request completes once the process AND both pipes
 * have closed (3 handles); then we wake the parked coroutine (or post a semaphore for a non-coroutine
 * caller). libuv reads both pipes on the loop, so there is no read-order deadlock. */
typedef struct rf_proc_state {
    uv_process_t proc;
    uv_pipe_t out_pipe;
    uv_pipe_t err_pipe;
    char** argv;               /* owned, NULL-terminated argv (each entry malloc'd) */
    char* cwd;                 /* owned working directory, or NULL to inherit */
    char** envp;               /* owned, NULL-terminated "KEY=VALUE" env, or NULL to inherit */
    int handles_open;          /* proc + 2 pipes still open; finalize when this hits 0 */
    int64_t exit_status;       /* child exit code (meaningful when term_signal == 0) */
    int term_signal;           /* signal that killed the child, or 0 if it exited normally */
    int spawn_err;             /* non-zero libuv error if uv_spawn failed outright */
    char* out_buf; size_t out_len; size_t out_cap;
    char* err_buf; size_t err_len; size_t err_cap;
    uv_sem_t sem;              /* non-coroutine caller blocks on this; coroutine uses the scheduler */
    int use_sem;               /* 1 if a non-coroutine caller is waiting on sem */
} rf_proc_state;

/* Grow-and-append into a malloc'd, NUL-terminated buffer. Best-effort on OOM (drops the chunk). */
static void rf_proc_buf_append(char** buf, size_t* len, size_t* cap, const char* data, size_t n)
{
    if (*len + n + 1 > *cap) {
        size_t ncap = (*cap == 0) ? 256 : *cap;
        while (*len + n + 1 > ncap) { ncap *= 2; }
        char* nb = (char*)realloc(*buf, ncap);
        if (nb == NULL) { return; }
        *buf = nb;
        *cap = ncap;
    }
    memcpy(*buf + *len, data, n);
    *len += n;
    (*buf)[*len] = '\0';
}

static void rf_proc_finalize(rf_io_req* req)
{
    rf_proc_state* st = req->proc;
    req->done = 1;
    if (req->coro != NULL && req->sched != NULL) {
        rf_sched_wake(req->sched, req->coro);  /* coroutine caller: woken via the scheduler */
    } else if (st->use_sem) {
        uv_sem_post(&st->sem);                 /* non-coroutine caller: release the blocking wait */
    }
}

static void rf_proc_close_cb(uv_handle_t* h)
{
    rf_io_req* req = (rf_io_req*)h->data;
    rf_proc_state* st = req->proc;
    st->handles_open--;
    if (st->handles_open == 0) {
        rf_proc_finalize(req);
    }
}

static void rf_proc_alloc_cb(uv_handle_t* h, size_t suggested, uv_buf_t* buf)
{
    (void)h;
    *buf = uv_buf_init((char*)malloc(suggested), (unsigned int)suggested);
}

static void rf_proc_read_cb(uv_stream_t* stream, ssize_t nread, const uv_buf_t* buf)
{
    rf_io_req* req = (rf_io_req*)stream->data;
    rf_proc_state* st = req->proc;
    if (nread > 0) {
        if (stream == (uv_stream_t*)&st->out_pipe) {
            rf_proc_buf_append(&st->out_buf, &st->out_len, &st->out_cap, buf->base, (size_t)nread);
        } else {
            rf_proc_buf_append(&st->err_buf, &st->err_len, &st->err_cap, buf->base, (size_t)nread);
        }
    }
    if (buf->base != NULL) { free(buf->base); }
    if (nread < 0) { /* UV_EOF or error: the child closed this stream */
        uv_read_stop(stream);
        uv_close((uv_handle_t*)stream, rf_proc_close_cb);
    }
}

static void rf_proc_exit_cb(uv_process_t* proc, int64_t exit_status, int term_signal)
{
    rf_io_req* req = (rf_io_req*)proc->data;
    rf_proc_state* st = req->proc;
    st->exit_status = exit_status;
    st->term_signal = term_signal;
    uv_close((uv_handle_t*)proc, rf_proc_close_cb);
}

/* Loop thread: launch the process. On spawn failure no exit_cb fires, so we close the (init'd but
 * unused) pipes and let their close callbacks finalize with the error. */
static void rf_proc_start(rf_io_req* req)
{
    rf_proc_state* st = req->proc;
    rf_io_loop* io = &g_io;

    uv_pipe_init(&io->loop, &st->out_pipe, 0); st->out_pipe.data = req;
    uv_pipe_init(&io->loop, &st->err_pipe, 0); st->err_pipe.data = req;

    uv_stdio_container_t stdio[3];
    stdio[0].flags = UV_IGNORE;
    stdio[1].flags = (uv_stdio_flags)(UV_CREATE_PIPE | UV_WRITABLE_PIPE);
    stdio[1].data.stream = (uv_stream_t*)&st->out_pipe;
    stdio[2].flags = (uv_stdio_flags)(UV_CREATE_PIPE | UV_WRITABLE_PIPE);
    stdio[2].data.stream = (uv_stream_t*)&st->err_pipe;

    uv_process_options_t opts;
    memset(&opts, 0, sizeof opts);
    opts.exit_cb = rf_proc_exit_cb;
    opts.file = st->argv[0];
    opts.args = st->argv;
    opts.cwd = st->cwd; /* NULL = inherit the parent's working directory */
    opts.env = st->envp; /* NULL = inherit the parent's environment */
    opts.stdio = stdio;
    opts.stdio_count = 3;

    st->proc.data = req;
    int r = uv_spawn(&io->loop, &st->proc, &opts);
    if (r != 0) {
        st->spawn_err = r;
        st->exit_status = -1;
        st->term_signal = 0;
        st->handles_open = 2; /* only the two pipes exist (proc never started) */
        uv_close((uv_handle_t*)&st->out_pipe, rf_proc_close_cb);
        uv_close((uv_handle_t*)&st->err_pipe, rf_proc_close_cb);
        return;
    }
    st->handles_open = 3; /* proc + 2 pipes */
    uv_read_start((uv_stream_t*)&st->out_pipe, rf_proc_alloc_cb, rf_proc_read_cb);
    uv_read_start((uv_stream_t*)&st->err_pipe, rf_proc_alloc_cb, rf_proc_read_cb);
}

/* Loop thread: drain the submission queue and start each request. File I/O goes to the threadpool
 * (uv_queue_work); a subprocess is launched directly on the loop (uv_spawn). Both must run on the
 * loop thread, so submission hops here via uv_async_send. */
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
        if (req->kind == RF_IO_RUN_PROCESS) {
            rf_proc_start(req);
        } else {
            req->work.data = req;
            uv_queue_work(&io->loop, &req->work, rf_io_work_cb, rf_io_after_cb);
        }
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
        /* The I/O threadpool will wake us cross-thread (rf_io_after_cb -> rf_sched_wake): arm a
         * cross-waker so the run loop does not read this park as a deadlock, and disarm on resume. */
        rf_sched_arm_cross_waker(sched);
        /* Park EXACTLY ONCE to pair with the exactly-once wake from rf_io_after_cb. Do NOT loop on
         * req->done: the work can complete (and wake us) before we park, in which case rf_sched_wake
         * already queued us to ready and a single rf_sched_park_external switch-out is resumed from
         * there. Looping would skip the park when done is already set, leaving us spuriously linked
         * in the ready queue -> use-after-free once the coroutine completes and is freed. The wake
         * sets req->done before signalling, so after the resume req->done is reliably 1. */
        rf_sched_park_external();
        rf_sched_disarm_cross_waker(sched);
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

/* Captured subprocess result, handed to RF after rf_proc_run returns. Thread-local for the same
 * reason as g_io_last_len: set on the scheduler thread immediately before rf_proc_run returns and
 * read by RF right after, with no other coroutine running in between. */
static _Thread_local int64_t    g_proc_exit = 0;
static _Thread_local int        g_proc_signal = 0;     /* 0 = exited normally (not signalled) */
static _Thread_local char*      g_proc_out = NULL;
static _Thread_local rf_address g_proc_out_len = 0;
static _Thread_local char*      g_proc_err = NULL;
static _Thread_local rf_address g_proc_err_len = 0;

rf_S32     rf_proc_term_signal(void) { return (rf_S32)g_proc_signal; }
char*      rf_proc_output(void)      { return g_proc_out; }
rf_address rf_proc_output_len(void)  { return g_proc_out_len; }
char*      rf_proc_errors(void)      { return g_proc_err; }
rf_address rf_proc_errors_len(void)  { return g_proc_err_len; }

/* Duplicate `n` bytes of `s` (or strlen(s) if n < 0) into a fresh NUL-terminated string. */
static char* rf_strdupn(const char* s, rf_S32 n)
{
    size_t len = (n < 0) ? strlen(s) : (size_t)n;
    char* d = (char*)malloc(len + 1);
    if (d == NULL) { return NULL; }
    memcpy(d, s, len);
    d[len] = '\0';
    return d;
}

static rf_proc_state* rf_proc_state_new(void)
{
    rf_proc_state* st = (rf_proc_state*)calloc(1, sizeof(rf_proc_state));
    if (st == NULL) {
        __rf_throw("OutOfMemoryError", "Failed to allocate subprocess state");
        return NULL; /* unreachable */
    }
    st->exit_status = -1;
    st->term_signal = 0;
    return st;
}

static void rf_proc_state_free(rf_proc_state* st)
{
    if (st == NULL) { return; }
    if (st->argv != NULL) {
        for (int i = 0; st->argv[i] != NULL; i++) { free(st->argv[i]); }
        free(st->argv);
    }
    if (st->envp != NULL) {
        for (int i = 0; st->envp[i] != NULL; i++) { free(st->envp[i]); }
        free(st->envp);
    }
    free(st->cwd);
    /* out_buf / err_buf are handed to the RF caller via the thread-locals — NOT freed here. */
    free(st);
}

/* Run a fully-prepared state (argv + optional cwd set), capturing stdout/stderr. Inside a
 * scheduler-driven coroutine the coroutine PARKS while the process runs (siblings progress); on a
 * plain thread it blocks on a semaphore. Publishes the captured streams + status to the thread-locals,
 * frees the state (takes ownership), and returns the child's exit code (-1 if it could not be spawned). */
static int64_t rf_proc_exec(rf_proc_state* st)
{
    rf_io_req* req = (rf_io_req*)calloc(1, sizeof(rf_io_req));
    if (req == NULL) {
        rf_proc_state_free(st);
        __rf_throw("OutOfMemoryError", "Failed to allocate subprocess request");
        return -1; /* unreachable */
    }
    req->kind = RF_IO_RUN_PROCESS;
    req->proc = st;

    rf_sched* sched = g_io_ok ? rf_sched_current() : NULL;
    rf_coro* self = g_io_ok ? rf_coro_current() : NULL;

    if (sched != NULL && self != NULL) {
        req->sched = sched;
        req->coro = self;
        rf_io_submit(req);
        /* Woken cross-thread exactly once by rf_proc_finalize: arm/disarm a cross-waker around the
         * park so the run loop does not mistake it for a deadlock. */
        rf_sched_arm_cross_waker(sched);
        rf_sched_park_external();   /* woken exactly once by rf_proc_finalize */
        rf_sched_disarm_cross_waker(sched);
    } else if (g_io_ok) {
        st->use_sem = 1;
        uv_sem_init(&st->sem, 0);
        rf_io_submit(req);
        uv_sem_wait(&st->sem);      /* non-coroutine caller: block until the loop thread finalizes */
        uv_sem_destroy(&st->sem);
    }

    g_proc_exit    = st->exit_status;
    g_proc_signal  = st->term_signal;
    g_proc_out     = st->out_buf;
    g_proc_out_len = (rf_address)st->out_len;
    g_proc_err     = st->err_buf;
    g_proc_err_len = (rf_address)st->err_len;

    int64_t exit_code = (st->spawn_err != 0) ? -1 : st->exit_status;
    rf_proc_state_free(st);
    free(req);
    return exit_code;
}

/* Shell form: run `command` via the platform shell (sh -c / cmd /c). Convenience for pipes/globs at
 * the cost of shell quoting; prefer the argv builder (rf_proc_begin/...) for untrusted input. */
int64_t rf_proc_run(const char* command, rf_S32 command_len)
{
    uv_once(&g_io_once, rf_io_init_once); /* normally already done by rf_io_runtime_init */
    rf_proc_state* st = rf_proc_state_new();
    st->argv = (char**)calloc(4, sizeof(char*));
    if (st->argv == NULL) {
        rf_proc_state_free(st);
        __rf_throw("OutOfMemoryError", "Failed to allocate subprocess argv");
        return -1; /* unreachable */
    }
#if defined(_WIN32)
    st->argv[0] = rf_strdupn("cmd.exe", -1); st->argv[1] = rf_strdupn("/c", -1);
#else
    st->argv[0] = rf_strdupn("/bin/sh", -1); st->argv[1] = rf_strdupn("-c", -1);
#endif
    st->argv[2] = rf_strdupn(command, command_len);
    st->argv[3] = NULL;
    return rf_proc_exec(st);
}

/* Argv builder: spawn an executable directly with an explicit argument vector (NO shell — no quoting
 * or injection hazard). rf_proc_begin sets argv[0] = file; add_arg appends; set_cwd is optional;
 * run_built launches and consumes the builder. Mirrors the rf_race_* incremental-FFI pattern. */
typedef struct rf_proc_builder {
    char** argv;
    int argv_count;
    int argv_cap;
    char* cwd;
    char** envv;               /* "KEY=VALUE" override strings (not NULL-terminated; env_count) */
    int env_count;
    int env_cap;
} rf_proc_builder;

/* The parent process environment as a NULL-terminated "KEY=VALUE" array. NOTE (Windows): this is the
 * CRT's ANSI environment — correct for ASCII keys/values (PATH, etc.); non-ASCII values could be
 * mis-encoded since uv treats opts.env as UTF-8. Acceptable for the common case. */
#if defined(_WIN32)
extern char** _environ;
  #define RF_PARENT_ENVIRON _environ
#else
extern char** environ;
  #define RF_PARENT_ENVIRON environ
#endif

/* Build a NULL-terminated env array = parent environment with `overrides` applied (an override
 * replaces a parent entry with the same KEY, else is appended). The override strings are MOVED into
 * the result; parent entries are copied. Returns NULL if there are no overrides (inherit). */
static char** rf_proc_merge_env(char** overrides, int n)
{
    if (n <= 0) { return NULL; }
    char** parent = RF_PARENT_ENVIRON;
    int pc = 0;
    if (parent != NULL) { while (parent[pc] != NULL) { pc++; } }
    char** env = (char**)malloc((size_t)(pc + n + 1) * sizeof(char*));
    if (env == NULL) { return NULL; }
    int k = 0;
    for (int i = 0; i < pc; i++) {
        const char* peq = strchr(parent[i], '=');
        size_t pkl = peq ? (size_t)(peq - parent[i]) : strlen(parent[i]);
        int overridden = 0;
        for (int j = 0; j < n; j++) {
            const char* oeq = strchr(overrides[j], '=');
            size_t okl = oeq ? (size_t)(oeq - overrides[j]) : strlen(overrides[j]);
            if (okl == pkl && memcmp(overrides[j], parent[i], pkl) == 0) { overridden = 1; break; }
        }
        if (!overridden) { env[k++] = rf_strdupn(parent[i], -1); }
    }
    for (int j = 0; j < n; j++) { env[k++] = overrides[j]; } /* move the override strings in */
    env[k] = NULL;
    return env;
}

rf_proc_builder* rf_proc_begin(const char* file, rf_S32 file_len)
{
    uv_once(&g_io_once, rf_io_init_once);
    rf_proc_builder* b = (rf_proc_builder*)calloc(1, sizeof(rf_proc_builder));
    if (b == NULL) {
        __rf_throw("OutOfMemoryError", "Failed to allocate subprocess builder");
        return NULL; /* unreachable */
    }
    b->argv_cap = 4;
    b->argv = (char**)calloc((size_t)b->argv_cap, sizeof(char*));
    if (b->argv == NULL) {
        free(b);
        __rf_throw("OutOfMemoryError", "Failed to allocate subprocess argv");
        return NULL; /* unreachable */
    }
    b->argv[0] = rf_strdupn(file, file_len); /* argv[0] = the executable */
    b->argv_count = 1;
    return b;
}

void rf_proc_add_arg(rf_proc_builder* b, const char* arg, rf_S32 arg_len)
{
    if (b == NULL) { return; }
    if (b->argv_count + 2 > b->argv_cap) { /* +1 for the new arg, +1 for the trailing NULL */
        int ncap = b->argv_cap * 2;
        char** na = (char**)realloc(b->argv, (size_t)ncap * sizeof(char*));
        if (na == NULL) { return; }
        b->argv = na;
        b->argv_cap = ncap;
    }
    b->argv[b->argv_count] = rf_strdupn(arg, arg_len);
    b->argv_count++;
    b->argv[b->argv_count] = NULL;
}

void rf_proc_set_cwd(rf_proc_builder* b, const char* cwd, rf_S32 cwd_len)
{
    if (b == NULL) { return; }
    free(b->cwd);
    b->cwd = NULL;
    size_t len = (cwd_len < 0) ? strlen(cwd) : (size_t)cwd_len;
    if (len > 0) { b->cwd = rf_strdupn(cwd, (rf_S32)len); } /* empty = inherit (leave NULL) */
}

/* Add an environment override "KEY=VALUE". These are MERGED into (not replacing) the parent
 * environment at launch — so setting one var keeps PATH and the rest intact. */
void rf_proc_add_env(rf_proc_builder* b, const char* key, rf_S32 key_len, const char* val, rf_S32 val_len)
{
    if (b == NULL) { return; }
    if (b->env_count + 1 > b->env_cap) {
        int ncap = (b->env_cap == 0) ? 4 : b->env_cap * 2;
        char** ne = (char**)realloc(b->envv, (size_t)ncap * sizeof(char*));
        if (ne == NULL) { return; }
        b->envv = ne;
        b->env_cap = ncap;
    }
    size_t kl = (key_len < 0) ? strlen(key) : (size_t)key_len;
    size_t vl = (val_len < 0) ? strlen(val) : (size_t)val_len;
    char* entry = (char*)malloc(kl + 1 + vl + 1);
    if (entry == NULL) { return; }
    memcpy(entry, key, kl);
    entry[kl] = '=';
    memcpy(entry + kl + 1, val, vl);
    entry[kl + 1 + vl] = '\0';
    b->envv[b->env_count] = entry;
    b->env_count++;
}

int64_t rf_proc_run_built(rf_proc_builder* b)
{
    if (b == NULL) { return -1; }
    rf_proc_state* st = rf_proc_state_new();
    st->argv = b->argv; /* transfer ownership of the array + strings */
    st->cwd = b->cwd;
    st->envp = rf_proc_merge_env(b->envv, b->env_count); /* moves override strings into st->envp */
    free(b->envv); /* the override strings were moved into st->envp; free only the array */
    free(b);
    return rf_proc_exec(st);
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

/* Synchronous subprocess fallback (no libuv): popen captures stdout only; no stderr split, no
 * signal info. Provides the symbols so the surface exists without the async backend. */
static _Thread_local char*      g_proc_out = NULL;
static _Thread_local rf_address g_proc_out_len = 0;

rf_S32     rf_proc_term_signal(void) { return 0; }
char*      rf_proc_output(void)      { return g_proc_out; }
rf_address rf_proc_output_len(void)  { return g_proc_out_len; }
char*      rf_proc_errors(void)      { return NULL; }
rf_address rf_proc_errors_len(void)  { return 0; }

int64_t rf_proc_run(const char* command, rf_S32 command_len)
{
    (void)command_len;
    g_proc_out = NULL;
    g_proc_out_len = 0;
#if defined(_WIN32)
    FILE* p = _popen(command, "r");
#else
    FILE* p = popen(command, "r");
#endif
    if (p == NULL) { return -1; }
    char* buf = NULL; size_t len = 0; size_t cap = 0;
    char chunk[4096];
    size_t r;
    while ((r = fread(chunk, 1, sizeof chunk, p)) > 0) {
        if (len + r + 1 > cap) {
            size_t ncap = (cap == 0) ? 4096 : cap;
            while (len + r + 1 > ncap) { ncap *= 2; }
            char* nb = (char*)realloc(buf, ncap);
            if (nb == NULL) { break; }
            buf = nb; cap = ncap;
        }
        memcpy(buf + len, chunk, r);
        len += r;
    }
    if (buf != NULL) { buf[len] = '\0'; }
#if defined(_WIN32)
    int status = _pclose(p);
#else
    int status = pclose(p);
#endif
    g_proc_out = buf;
    g_proc_out_len = (rf_address)len;
    return (int64_t)status;
}

/* Argv builder fallback (no libuv): accumulate a space-joined command string and run it via popen.
 * This loses the no-shell safety of the real builder, but the fallback only applies when the async
 * backend is absent. cwd is ignored here. */
typedef struct rf_proc_builder {
    char* cmd; size_t len; size_t cap;
} rf_proc_builder;

static void rf_proc_fb_append(rf_proc_builder* b, const char* s, rf_S32 n)
{
    size_t add = (n < 0) ? strlen(s) : (size_t)n;
    if (b->len + add + 2 > b->cap) {
        size_t ncap = (b->cap == 0) ? 256 : b->cap;
        while (b->len + add + 2 > ncap) { ncap *= 2; }
        char* nb = (char*)realloc(b->cmd, ncap);
        if (nb == NULL) { return; }
        b->cmd = nb; b->cap = ncap;
    }
    if (b->len > 0) { b->cmd[b->len++] = ' '; }
    memcpy(b->cmd + b->len, s, add);
    b->len += add;
    b->cmd[b->len] = '\0';
}

rf_proc_builder* rf_proc_begin(const char* file, rf_S32 file_len)
{
    rf_proc_builder* b = (rf_proc_builder*)calloc(1, sizeof(rf_proc_builder));
    if (b == NULL) { return NULL; }
    rf_proc_fb_append(b, file, file_len);
    return b;
}
void rf_proc_add_arg(rf_proc_builder* b, const char* arg, rf_S32 arg_len) { if (b) { rf_proc_fb_append(b, arg, arg_len); } }
void rf_proc_set_cwd(rf_proc_builder* b, const char* cwd, rf_S32 cwd_len) { (void)b; (void)cwd; (void)cwd_len; }
void rf_proc_add_env(rf_proc_builder* b, const char* key, rf_S32 key_len, const char* val, rf_S32 val_len) { (void)b; (void)key; (void)key_len; (void)val; (void)val_len; }
int64_t rf_proc_run_built(rf_proc_builder* b)
{
    if (b == NULL) { return -1; }
    int64_t code = rf_proc_run(b->cmd != NULL ? b->cmd : "", -1);
    free(b->cmd);
    free(b);
    return code;
}

#endif /* HAVE_LIBUV */