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