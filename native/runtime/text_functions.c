/**
 * @file text_functions.c
 * @brief Text formatting and parsing functions for RazorForge runtime
 *
 * Provides rf_format_X and rf_parse_X functions for all primitive types.
 * Function names use PascalCase type identifiers to match RazorForge conventions.
 *
 * NOTE: Text in RazorForge is UTF-32 encoded. These functions currently return
 * heap-allocated C strings (ASCII/UTF-8) which is sufficient for numeric formatting
 * since all numeric characters are in the ASCII range. The codegen layer handles
 * conversion to the internal Text representation.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <inttypes.h>
#include <errno.h>
#include <limits.h>
#include <float.h>
#include <math.h>
#include "types.h"

// Forward declaration from f16_functions.c
extern float rf_f16_to_f32(uint16_t x);
extern uint16_t rf_f16_from_f32(float x);

// ============================================================================
// Formatting: Signed Integers
// ============================================================================

rf_UAddr rf_format_S8(rf_S8 value)
{
    char* buffer = (char*)malloc(8);
    if (!buffer) return 0;
    snprintf(buffer, 8, "%" PRId8, value);
    return (rf_UAddr)buffer;
}

rf_UAddr rf_format_S16(rf_S16 value)
{
    char* buffer = (char*)malloc(8);
    if (!buffer) return 0;
    snprintf(buffer, 8, "%" PRId16, value);
    return (rf_UAddr)buffer;
}

rf_UAddr rf_format_S32(rf_S32 value)
{
    char* buffer = (char*)malloc(16);
    if (!buffer) return 0;
    snprintf(buffer, 16, "%" PRId32, value);
    return (rf_UAddr)buffer;
}

rf_UAddr rf_format_S64(rf_S64 value)
{
    char* buffer = (char*)malloc(32);
    if (!buffer) return 0;
    snprintf(buffer, 32, "%" PRId64, value);
    return (rf_UAddr)buffer;
}

rf_UAddr rf_format_SAddr(rf_SAddr value)
{
    char* buffer = (char*)malloc(32);
    if (!buffer) return 0;
    snprintf(buffer, 32, "%" PRIdPTR, value);
    return (rf_UAddr)buffer;
}

// S128/U128 format functions use split (low, high) parameters to avoid
// ABI mismatch: LLVM passes i128 in RCX:RDX, but MSVC ABI passes by pointer.
rf_UAddr rf_format_U128(uint64_t low, uint64_t high)
{
    rf_U128 value = ((rf_U128)high << 64) | (rf_U128)low;
    char buf[40]; // max 39 digits + null
    char* p = buf + sizeof(buf);
    *--p = '\0';
    if (value == 0) {
        *--p = '0';
    } else {
        while (value > 0) {
            *--p = '0' + (char)(value % 10);
            value /= 10;
        }
    }
    size_t len = (size_t)(buf + sizeof(buf) - p);
    char* result = (char*)malloc(len);
    if (!result) return 0;
    memcpy(result, p, len);
    return (rf_UAddr)result;
}

rf_UAddr rf_format_S128(uint64_t low, uint64_t high)
{
    rf_S128 value = (rf_S128)(((rf_U128)high << 64) | (rf_U128)low);
    if (value >= 0) return rf_format_U128(low, high);
    char buf[41]; // '-' + max 39 digits + null
    char* p = buf + sizeof(buf);
    *--p = '\0';
    rf_U128 uval = -((rf_U128)value);
    while (uval > 0) {
        *--p = '0' + (char)(uval % 10);
        uval /= 10;
    }
    *--p = '-';
    size_t len = (size_t)(buf + sizeof(buf) - p);
    char* result = (char*)malloc(len);
    if (!result) return 0;
    memcpy(result, p, len);
    return (rf_UAddr)result;
}

// ============================================================================
// Formatting: Unsigned Integers
// ============================================================================

rf_UAddr rf_format_U8(rf_U8 value)
{
    char* buffer = (char*)malloc(8);
    if (!buffer) return 0;
    snprintf(buffer, 8, "%" PRIu8, value);
    return (rf_UAddr)buffer;
}

rf_UAddr rf_format_U16(rf_U16 value)
{
    char* buffer = (char*)malloc(8);
    if (!buffer) return 0;
    snprintf(buffer, 8, "%" PRIu16, value);
    return (rf_UAddr)buffer;
}

rf_UAddr rf_format_U32(rf_U32 value)
{
    char* buffer = (char*)malloc(16);
    if (!buffer) return 0;
    snprintf(buffer, 16, "%" PRIu32, value);
    return (rf_UAddr)buffer;
}

rf_UAddr rf_format_U64(rf_U64 value)
{
    char* buffer = (char*)malloc(32);
    if (!buffer) return 0;
    snprintf(buffer, 32, "%" PRIu64, value);
    return (rf_UAddr)buffer;
}

rf_UAddr rf_format_UAddr(rf_UAddr value)
{
    char* buffer = (char*)malloc(32);
    if (!buffer) return 0;
    snprintf(buffer, 32, "%" PRIuPTR, value);
    return (rf_UAddr)buffer;
}

// ============================================================================
// Formatting: Binary Floating Point
// ============================================================================

rf_UAddr rf_format_F16(rf_U16 value)
{
    char* buffer = (char*)malloc(64);
    if (!buffer) return 0;
    float f = rf_f16_to_f32(value);
    snprintf(buffer, 64, "%.4g", (double)f);
    return (rf_UAddr)buffer;
}

rf_UAddr rf_format_F32(float value)
{
    char* buffer = (char*)malloc(64);
    if (!buffer) return 0;
    snprintf(buffer, 64, "%.7g", (double)value);
    return (rf_UAddr)buffer;
}

rf_UAddr rf_format_F64(double value)
{
    char* buffer = (char*)malloc(64);
    if (!buffer) return 0;
    snprintf(buffer, 64, "%.15g", value);
    return (rf_UAddr)buffer;
}

// F128 formatting is in f128_functions.c (requires LibBF)

// ============================================================================
// Formatting: Bool
// ============================================================================

rf_UAddr rf_format_Bool(rf_Bool value)
{
    char* buffer = (char*)malloc(8);
    if (!buffer) return 0;
    memcpy(buffer, value ? "true" : "false", value ? 5 : 6);
    return (rf_UAddr)buffer;
}

// ============================================================================
// Parsing: Signed Integers
// ============================================================================

rf_S8 rf_parse_S8(const char* str)
{
    long val = strtol(str, NULL, 10);
    return (rf_S8)val;
}

rf_S16 rf_parse_S16(const char* str)
{
    long val = strtol(str, NULL, 10);
    return (rf_S16)val;
}

rf_S32 rf_parse_S32(const char* str)
{
    long val = strtol(str, NULL, 10);
    return (rf_S32)val;
}

rf_S64 rf_parse_S64(const char* str)
{
    return (rf_S64)strtoll(str, NULL, 10);
}

rf_SAddr rf_parse_SAddr(const char* str)
{
    return (rf_SAddr)strtoll(str, NULL, 10);
}

// Parse functions also need ABI-safe signatures.
// They return the 128-bit result via an sret pointer (first parameter).
// The codegen's NeedsCExternSret handles this automatically for i128 returns...
// but i128 isn't a Record/Tuple type so we need a different approach.
// Instead, we make the C functions return via split struct.
typedef struct { uint64_t low; uint64_t high; } split128_t;

split128_t rf_parse_U128(const char* str)
{
    rf_U128 result = 0;
    while (*str == ' ' || *str == '\t') str++;
    while (*str >= '0' && *str <= '9') {
        result = result * 10 + (rf_U128)(*str - '0');
        str++;
    }
    split128_t out;
    out.low = (uint64_t)result;
    out.high = (uint64_t)(result >> 64);
    return out;
}

split128_t rf_parse_S128(const char* str)
{
    while (*str == ' ' || *str == '\t') str++;
    int neg = 0;
    if (*str == '-') { neg = 1; str++; }
    else if (*str == '+') { str++; }
    split128_t uval = rf_parse_U128(str);
    if (neg) {
        rf_U128 v = ((rf_U128)uval.high << 64) | (rf_U128)uval.low;
        rf_S128 sv = -(rf_S128)v;
        uval.low = (uint64_t)sv;
        uval.high = (uint64_t)((rf_U128)sv >> 64);
    }
    return uval;
}

// ============================================================================
// Parsing: Unsigned Integers
// ============================================================================

rf_U8 rf_parse_U8(const char* str)
{
    unsigned long val = strtoul(str, NULL, 10);
    return (rf_U8)val;
}

rf_U16 rf_parse_U16(const char* str)
{
    unsigned long val = strtoul(str, NULL, 10);
    return (rf_U16)val;
}

rf_U32 rf_parse_U32(const char* str)
{
    unsigned long val = strtoul(str, NULL, 10);
    return (rf_U32)val;
}

rf_U64 rf_parse_U64(const char* str)
{
    return (rf_U64)strtoull(str, NULL, 10);
}

rf_UAddr rf_parse_UAddr(const char* str)
{
    return (rf_UAddr)strtoull(str, NULL, 10);
}

// ============================================================================
// Parsing: Binary Floating Point
// ============================================================================

rf_U16 rf_parse_F16(const char* str)
{
    float val = strtof(str, NULL);
    return rf_f16_from_f32(val);
}

float rf_parse_F32(const char* str)
{
    return strtof(str, NULL);
}

double rf_parse_F64(const char* str)
{
    return strtod(str, NULL);
}

// F128 parsing is in f128_functions.c (requires LibBF)