#include "types.h"
#include "../include/razorforge_runtime.h"

#include <stdlib.h>

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
    return (rf_async_runtime*)calloc(1, sizeof(rf_async_runtime));
}

void rf_async_runtime_destroy(rf_async_runtime* runtime)
{
    free(runtime);
}

int rf_async_runtime_run_once(rf_async_runtime* runtime)
{
    if (runtime == NULL) return 0;

#ifdef HAVE_LIBUV
    /*
     * libuv integration point:
     * - own a uv_loop_t inside this wrapper
     * - drive one scheduler tick or one poll iteration
     * - wake parked suspended routines when I/O/timers complete
     */
    return runtime->should_stop ? 0 : 1;
#else
    return 0;
#endif
}

int rf_async_runtime_run_default(rf_async_runtime* runtime)
{
    if (runtime == NULL) return 0;

#ifdef HAVE_LIBUV
    while (!runtime->should_stop)
    {
        if (!rf_async_runtime_run_once(runtime))
        {
            break;
        }
    }
    return 1;
#else
    return 0;
#endif
}

void rf_async_runtime_stop(rf_async_runtime* runtime)
{
    if (runtime == NULL) return;
    runtime->should_stop = true;
}
