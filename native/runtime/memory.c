/*
 * RazorForge Runtime - Memory Management
 * Native implementation of stack and heap slice operations
 */

#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>

// Type aliases for RazorForge integration
typedef uintptr_t rf_address;
typedef size_t rf_size_t;

/* Runtime error + stack trace + exit(RF_EXIT_CRASH) (stacktrace.c). On a heap-allocation failure we raise this
 * instead of returning NULL, which would otherwise be dereferenced and crash with no diagnosis. */
extern void __rf_throw(const char* error_type, const char* message);

static void rf_oom(const char* what, uint64_t bytes)
{
    char buf[128];
    snprintf(buf, sizeof(buf), "Failed to %s %llu bytes", what, (unsigned long long)bytes);
    __rf_throw("OutOfMemoryError", buf); /* prints + stack trace + exit(RF_EXIT_CRASH); does not return */
}

/*
 * Dynamic memory allocation with zero-initialization and error handling
 */
void* rf_allocate_dynamic(uint64_t bytes)
{
    void* ptr = calloc(bytes, 1);
    if (!ptr)
    {
        rf_oom("allocate", bytes);
        return NULL; /* unreachable: rf_oom exits */
    }
    return ptr;
}

/*
 * Dynamic memory allocation without zero-initialization
 * Caller must write before reading any allocated byte
 */
void* rf_allocate_dynamic_uninit(uint64_t bytes)
{
    void* ptr = malloc(bytes);
    if (!ptr)
    {
        rf_oom("allocate", bytes);
        return NULL; /* unreachable */
    }
    return ptr;
}

/*
 * Dynamic memory deallocation (null-safe)
 */
void rf_invalidate(void* ptr)
{
    if (ptr != NULL)
    {
        free(ptr);
    }
}

// Scratch region: a per-OS-thread stack of zeroed temporary allocations released in bulk.
// A caller takes a mark, allocates any number of blocks, and releases back to the mark, which frees
// every block allocated since. Built for computations whose intermediates freely alias one another
// (the Real engine shares limb buffers between temporaries), where per-block ownership would need
// refcounting on every temporary. Marks nest in stack order. The region is thread-local, so a
// computation must not suspend between its mark and release (the Real engine never does).
typedef struct rf_region_block
{
    struct rf_region_block* prev;
} rf_region_block;

static _Thread_local rf_region_block* g_region_top = NULL;

// Returns the current region top. Pass it to rf_region_release to free everything allocated after it.
uint64_t rf_region_mark(void)
{
    return (uint64_t)(uintptr_t)g_region_top;
}

// Allocates `bytes` zeroed bytes in the current thread's scratch region.
void* rf_region_alloc(uint64_t bytes)
{
    rf_region_block* block = (rf_region_block*)calloc(1, sizeof(rf_region_block) + bytes);
    if (!block)
    {
        rf_oom("allocate scratch", bytes);
        return NULL; // unreachable: rf_oom exits
    }
    block->prev = g_region_top;
    g_region_top = block;
    return (void*)(block + 1);
}

// Frees every scratch block allocated since `mark` was taken.
void rf_region_release(uint64_t mark)
{
    rf_region_block* stop = (rf_region_block*)(uintptr_t)mark;
    while (g_region_top != NULL && g_region_top != stop)
    {
        rf_region_block* prev = g_region_top->prev;
        free(g_region_top);
        g_region_top = prev;
    }
}

/*
 * Dynamic memory reallocation with error handling
 */
void* rf_reallocate_dynamic(void* ptr, uint64_t bytes)
{
    void* new_ptr = realloc(ptr, bytes);

    if (!new_ptr && bytes != 0) /* realloc to 0 returning NULL is a valid free, not a failure */
    {
        rf_oom("reallocate to", bytes);
        return NULL; /* unreachable */
    }

    return new_ptr;
}

/*
 * Generic memory copy operation
 */
void rf_copy_bytes_at(rf_address dst_address, rf_address src_address, rf_address bytes)
{
    if (src_address == 0 || dst_address == 0 || bytes == 0)
    {
        return;
    }

    void* src = (void*)src_address;
    void* dst = (void*)dst_address;

    memmove(dst, src, bytes); // Use memmove for overlapping regions
}

/*
 * Fill memory region with a byte value
 */
void rf_set_bytes_at(rf_address dest_address, uint8_t value, uint64_t bytes)
{
    if (dest_address == 0 || bytes == 0)
    {
        return;
    }

    memset((void*)dest_address, value, (size_t)bytes);
}
