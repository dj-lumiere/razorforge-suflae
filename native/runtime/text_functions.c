/**
 * @file text_functions.c
 * @brief Text formatting functions for RazorForge runtime
 *
 * Provides string formatting and conversion functions for various types.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "types.h"

/**
 * Format an s64 (signed 64-bit integer) as a text string.
 *
 * @param value The s64 value to format
 * @return Pointer to a heap-allocated string containing the formatted value
 *
 * @note The caller is responsible for freeing the returned string
 */
rf_uaddr format_s64(rf_s64 value)
{
    // Allocate buffer for the string (max 21 chars: "-9223372036854775808" + null terminator)
    char* buffer = (char*)malloc(32);
    if (buffer == NULL)
    {
        return 0;
    }

    // Format the integer as a string
    snprintf(buffer, 32, "%lld", (long long)value);

    // Return the address as uaddr
    return (rf_uaddr)buffer;
}

/**
 * Format a u64 (unsigned 64-bit integer) as a text string.
 *
 * @param value The u64 value to format
 * @return Pointer to a heap-allocated string containing the formatted value
 *
 * @note The caller is responsible for freeing the returned string
 */
rf_uaddr format_u64(rf_u64 value)
{
    // Allocate buffer for the string (max 20 chars: "18446744073709551615" + null terminator)
    char* buffer = (char*)malloc(32);
    if (buffer == NULL)
    {
        return 0;
    }

    // Format the integer as a string
    snprintf(buffer, 32, "%llu", (unsigned long long)value);

    // Return the address as uaddr
    return (rf_uaddr)buffer;
}

/**
 * Format an s32 (signed 32-bit integer) as a text string.
 *
 * @param value The s32 value to format
 * @return Pointer to a heap-allocated string containing the formatted value
 *
 * @note The caller is responsible for freeing the returned string
 */
rf_uaddr format_s32(rf_s32 value)
{
    // Allocate buffer for the string (max 12 chars: "-2147483648" + null terminator)
    char* buffer = (char*)malloc(16);
    if (buffer == NULL)
    {
        return 0;
    }

    // Format the integer as a string
    snprintf(buffer, 16, "%d", value);

    // Return the address as uaddr
    return (rf_uaddr)buffer;
}

/**
 * Format a u32 (unsigned 32-bit integer) as a text string.
 *
 * @param value The u32 value to format
 * @return Pointer to a heap-allocated string containing the formatted value
 *
 * @note The caller is responsible for freeing the returned string
 */
rf_uaddr format_u32(rf_u32 value)
{
    // Allocate buffer for the string (max 10 chars: "4294967295" + null terminator)
    char* buffer = (char*)malloc(16);
    if (buffer == NULL)
    {
        return 0;
    }

    // Format the integer as a string
    snprintf(buffer, 16, "%u", value);

    // Return the address as uaddr
    return (rf_uaddr)buffer;
}

/**
 * Format an f64 (double precision float) as a text string.
 *
 * @param value The f64 value to format
 * @return Pointer to a heap-allocated string containing the formatted value
 *
 * @note The caller is responsible for freeing the returned string
 */
rf_uaddr format_f64(double value)
{
    // Allocate buffer for the string (sufficient for double precision)
    char* buffer = (char*)malloc(64);
    if (buffer == NULL)
    {
        return 0;
    }

    // Format the float as a string
    snprintf(buffer, 64, "%.15g", value);

    // Return the address as uaddr
    return (rf_uaddr)buffer;
}

/**
 * Format an f32 (single precision float) as a text string.
 *
 * @param value The f32 value to format
 * @return Pointer to a heap-allocated string containing the formatted value
 *
 * @note The caller is responsible for freeing the returned string
 */
rf_uaddr format_f32(float value)
{
    // Allocate buffer for the string (sufficient for single precision)
    char* buffer = (char*)malloc(64);
    if (buffer == NULL)
    {
        return 0;
    }

    // Format the float as a string
    snprintf(buffer, 64, "%.7g", (double)value);

    // Return the address as uaddr
    return (rf_uaddr)buffer;
}

/**
 * Format a bool as a text string ("true" or "false").
 *
 * @param value The bool value to format
 * @return Pointer to a heap-allocated string containing the formatted value
 *
 * @note The caller is responsible for freeing the returned string
 */
rf_uaddr format_bool(rf_bool value)
{
    // Allocate buffer for the string
    char* buffer = (char*)malloc(8);
    if (buffer == NULL)
    {
        return 0;
    }

    // Copy the appropriate string
    if (value)
    {
        strcpy(buffer, "true");
    }
    else
    {
        strcpy(buffer, "false");
    }

    // Return the address as uaddr
    return (rf_uaddr)buffer;
}
