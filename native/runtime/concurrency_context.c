#include "types.h"
#include "../include/razorforge_runtime.h"

#include <stdlib.h>

struct rf_context_runtime
{
    rf_U64 reserved;
};

const char* rf_context_backend_name(void)
{
#ifdef HAVE_LIBCO
    return "libco";
#else
    return "none";
#endif
}

rf_runtime_backend_state rf_context_backend_state(void)
{
#ifdef HAVE_LIBCO
    return RF_RUNTIME_BACKEND_AVAILABLE;
#else
    return RF_RUNTIME_BACKEND_UNAVAILABLE;
#endif
}

rf_context_runtime* rf_context_runtime_create(void)
{
    return (rf_context_runtime*)calloc(1, sizeof(rf_context_runtime));
}

void rf_context_runtime_destroy(rf_context_runtime* runtime)
{
    free(runtime);
}

int rf_context_runtime_spawn(rf_context_runtime* runtime, rf_context_entry_fn entry, void* userdata, size_t stack_size)
{
    (void)runtime;
    (void)stack_size;

#ifdef HAVE_LIBCO
    /*
     * libco integration point:
     * - create a stackful coroutine/fiber context
     * - register `entry(userdata)` as the bootstrap routine
     * - hand the resulting context to the RazorForge scheduler
     *
     * The wrapper boundary exists now so the rest of the runtime never calls
     * libco APIs directly.
     */
    if (entry == NULL) return 0;
    entry(userdata);
    return 1;
#else
    (void)entry;
    (void)userdata;
    return 0;
#endif
}
