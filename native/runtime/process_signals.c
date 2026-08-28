/*
 * process_signals.c — current-process signal handlers for RazorForge / Suflae.
 *
 * Backs the stdlib `Signals` module: `when_interrupted(handler)` (Ctrl-C / SIGINT) and
 * `when_terminated(handler)` (SIGTERM / console-close). This is DISTINCT from
 * async_io.c's `rf_proc_term_signal` (which reports the signal that killed a CHILD
 * process) and from signal_runtime.c's `SignalCaster` (a condition-variable monitor).
 *
 * Model (decided 2026-08-28 — dedicated dispatch thread + suppress-default):
 *
 *   1. Registering a handler installs an OS-level disposition that SUPPRESSES the
 *      default termination. The process no longer dies on Ctrl-C / SIGTERM by itself;
 *      the registered RF handler decides what to do (typically: set a flag its loop
 *      polls, then exit).
 *
 *   2. Handlers run OUTSIDE signal context, on a dedicated dispatch thread — never in
 *      the async-signal-unsafe context of the signal handler itself. This is the
 *      classic self-pipe trick on POSIX: the installed handler does nothing but
 *      `write()` one byte (async-signal-safe) to a pipe; a dedicated thread reads the
 *      pipe and invokes the registered RF closures. On Windows, SetConsoleCtrlHandler
 *      already dispatches on its own OS thread, so the handler runs the closures there.
 *
 * An RF handler is a `Routine[(), None]` value — a heap closure box whose first word is
 * the function pointer `void (*)(void* closure)` (the uniform lambda ABI; see
 * rf_cyclic_invoke_hook in coro_runtime.c). We store the box pointer (calloc'd by
 * rf_allocate_dynamic, never moved/freed) and invoke it by loading field 0 and passing
 * the box back as the hidden leading argument. Registration is process-lifetime: the
 * boxes are intentionally leaked (they live until exit), so no ownership dance is needed.
 */
#include "../include/razorforge_runtime.h"
#include "rf_sync.h"

#include <stdlib.h>
#include <string.h>

/* which-codes shared with the RF surface (Signals.rf) and the wire byte on the pipe. */
#define RF_SIG_INTERRUPT 0
#define RF_SIG_TERMINATE 1

/* One registered handler: its RF closure box plus an optional context handle.
 *   has_ctx == 0 — a `Routine[(), None]` handler, invoked as `fn(box)`.
 *   has_ctx == 1 — a `Routine[(Ctx,), None]` handler (Ctx = Roamed[T] / Guarded[T,P], both a
 *                  single pointer), invoked as `fn(box, ctx)`. `ctx` is the handle's raw
 *                  controller pointer, retained by the RF wrapper for the process lifetime. */
typedef struct
{
    void* box;
    void* ctx;
    int has_ctx;
} rf_handler_entry;

/* A growable list of registered handlers. */
typedef struct
{
    rf_handler_entry* items;
    size_t count;
    size_t cap;
} rf_handler_list;

static rf_handler_list g_interrupt_handlers;
static rf_handler_list g_terminate_handlers;
static rf_mutex g_sig_lock;           /* guards the lists AND one-time init */
static int g_sig_lock_ready = 0;      /* g_sig_lock has been rf_mutex_init'd */
static int g_sig_installed = 0;       /* OS handlers + dispatch thread are live */

/* Invoke one registered handler through the uniform lambda ABI: field 0 of the box is the
 * function pointer, and the box is passed back as the hidden leading argument. A context
 * handler additionally receives its context handle as the (single) logical argument. */
static void rf_invoke_entry(const rf_handler_entry* e)
{
    if (e->box == NULL) {
        return;
    }
    void* fn = *(void**)e->box;
    if (e->has_ctx) {
        ((void (*)(void*, void*))fn)(e->box, e->ctx);
    } else {
        ((void (*)(void*))fn)(e->box);
    }
}

/* Snapshot a handler list under the lock, then invoke every handler with the lock
 * released (a handler may run arbitrary RF code, including registering more). */
static void rf_dispatch_handlers(rf_handler_list* list)
{
    size_t n = 0;
    rf_handler_entry* snapshot = NULL;
    rf_mutex_lock(&g_sig_lock);
    n = list->count;
    if (n > 0) {
        snapshot = (rf_handler_entry*)malloc(n * sizeof(rf_handler_entry));
        if (snapshot != NULL) {
            memcpy(snapshot, list->items, n * sizeof(rf_handler_entry));
        }
    }
    rf_mutex_unlock(&g_sig_lock);

    if (snapshot == NULL) {
        return;
    }
    for (size_t i = 0; i < n; i++) {
        rf_invoke_entry(&snapshot[i]);
    }
    free(snapshot);
}

/* Dispatch by which-code (called from the POSIX dispatch thread / the Windows handler). */
static void rf_dispatch_which(int which)
{
    rf_dispatch_handlers(which == RF_SIG_TERMINATE ? &g_terminate_handlers
                                                    : &g_interrupt_handlers);
}

/* ========================================================================== */
/* Platform: install OS handlers + start the dispatch mechanism.              */
/* ========================================================================== */

#if defined(_WIN32)

#include <windows.h>

/* Runs on an OS thread the console subsystem spawns for us — already outside any
 * async-signal context, so we invoke the RF handlers directly. Returning TRUE marks
 * the event handled and suppresses the default termination. */
static BOOL WINAPI rf_console_ctrl_handler(DWORD ctrl_type)
{
    switch (ctrl_type) {
        case CTRL_C_EVENT:
        case CTRL_BREAK_EVENT:
            rf_dispatch_which(RF_SIG_INTERRUPT);
            return TRUE;
        case CTRL_CLOSE_EVENT:
        case CTRL_LOGOFF_EVENT:
        case CTRL_SHUTDOWN_EVENT:
            rf_dispatch_which(RF_SIG_TERMINATE);
            return TRUE;
        default:
            return FALSE;
    }
}

/* Must be called with g_sig_lock held. */
static void rf_signals_install_locked(void)
{
    SetConsoleCtrlHandler(rf_console_ctrl_handler, TRUE);
    g_sig_installed = 1;
}

#else /* POSIX */

#include <signal.h>
#include <unistd.h>
#include <pthread.h>
#include <errno.h>

static int g_self_pipe[2] = { -1, -1 };  /* [0]=read (dispatch thread), [1]=write (handler) */

/* Async-signal-safe: write a single which-code byte to the self-pipe and return. All
 * real work happens on the dispatch thread that reads the pipe. */
static void rf_posix_signal_handler(int signo)
{
    unsigned char code = (signo == SIGTERM) ? RF_SIG_TERMINATE : RF_SIG_INTERRUPT;
    ssize_t rc;
    do {
        rc = write(g_self_pipe[1], &code, 1);
    } while (rc < 0 && errno == EINTR);
    (void)rc;
}

/* Reads which-codes off the self-pipe and dispatches the matching handlers. */
static void* rf_signal_dispatch_main(void* arg)
{
    (void)arg;
    for (;;) {
        unsigned char code;
        ssize_t rc = read(g_self_pipe[0], &code, 1);
        if (rc == 1) {
            rf_dispatch_which((int)code);
        } else if (rc < 0 && errno == EINTR) {
            continue;
        } else if (rc <= 0) {
            break;  /* pipe closed — should not happen in a running process */
        }
    }
    return NULL;
}

/* Must be called with g_sig_lock held. */
static void rf_signals_install_locked(void)
{
    if (pipe(g_self_pipe) != 0) {
        return;  /* leave uninstalled; a later register retries */
    }

    pthread_t tid;
    if (pthread_create(&tid, NULL, rf_signal_dispatch_main, NULL) != 0) {
        close(g_self_pipe[0]);
        close(g_self_pipe[1]);
        g_self_pipe[0] = g_self_pipe[1] = -1;
        return;
    }
    pthread_detach(tid);

    /* Install the disposition. SA_RESTART so an interrupted syscall in the main program
     * resumes rather than failing with EINTR. The handler runs on whichever thread the
     * kernel delivers to; it only writes a byte, so that is safe. */
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = rf_posix_signal_handler;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = SA_RESTART;
    sigaction(SIGINT, &sa, NULL);
    sigaction(SIGTERM, &sa, NULL);

    g_sig_installed = 1;
}

#endif

/* ========================================================================== */
/* Registration surface (called from Signals.rf via C::).                     */
/* ========================================================================== */

/* Ensure the lock exists exactly once. There is no portable static mutex initializer
 * that matches rf_mutex on both platforms, so guard the very first init with a simple
 * flag; the only pre-lock race window is the first two concurrent registrations, which
 * do not happen in practice (registration is startup wiring). */
static void rf_signals_ensure_lock(void)
{
    if (!g_sig_lock_ready) {
        rf_mutex_init(&g_sig_lock);
        g_sig_lock_ready = 1;
    }
}

static void rf_handler_list_append(rf_handler_list* list, rf_handler_entry entry)
{
    if (list->count == list->cap) {
        size_t new_cap = list->cap == 0 ? 4 : list->cap * 2;
        rf_handler_entry* grown =
            (rf_handler_entry*)realloc(list->items, new_cap * sizeof(rf_handler_entry));
        if (grown == NULL) {
            return;  /* out of memory: silently drop — handler registration is best-effort */
        }
        list->items = grown;
        list->cap = new_cap;
    }
    list->items[list->count++] = entry;
}

/* Shared registration: append `entry` to the class's list and install the OS handlers once. */
static void rf_signal_register_entry(int32_t which, rf_handler_entry entry)
{
    rf_signals_ensure_lock();
    rf_mutex_lock(&g_sig_lock);

    rf_handler_list_append(which == RF_SIG_TERMINATE ? &g_terminate_handlers
                                                     : &g_interrupt_handlers,
                           entry);

    if (!g_sig_installed) {
        rf_signals_install_locked();
    }

    rf_mutex_unlock(&g_sig_lock);
}

/* Register an RF `Routine[(), None]` closure box for the given signal class.
 * which: 0 = interrupt (SIGINT / Ctrl-C), 1 = terminate (SIGTERM / console-close). */
void rf_signal_register(int32_t which, void* handler_box)
{
    rf_handler_entry entry = { handler_box, NULL, 0 };
    rf_signal_register_entry(which, entry);
}

/* Register an RF `Routine[(Ctx,), None]` closure box plus a context handle (Roamed[T] /
 * Guarded[T,P] — a single pointer, retained for the process lifetime by the RF wrapper).
 * The handler receives `context` as its argument each time the signal fires. */
void rf_signal_register_ctx(int32_t which, void* handler_box, void* context)
{
    rf_handler_entry entry = { handler_box, context, 1 };
    rf_signal_register_entry(which, entry);
}
