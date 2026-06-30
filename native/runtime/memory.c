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

/* Runtime error + stack trace + exit(1) (stacktrace.c). On a heap-allocation failure we raise this
 * instead of returning NULL, which would otherwise be dereferenced and crash with no diagnosis. */
extern void __rf_throw(const char* error_type, const char* message);

static void rf_oom(const char* what, uint64_t bytes)
{
    char buf[128];
    snprintf(buf, sizeof(buf), "Failed to %s %llu bytes", what, (unsigned long long)bytes);
    __rf_throw("OutOfMemoryError", buf); /* prints + stack trace + exit(1); does not return */
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
