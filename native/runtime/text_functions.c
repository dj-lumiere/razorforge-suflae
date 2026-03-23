/**
 * @file text_functions.c
 * @brief Text formatting and parsing functions for RazorForge runtime
 *
 * Provides rf_format_X and rf_parse_X functions for floating-point types.
 * Integer formatting and parsing is now pure RazorForge (see Text.Convert.rf
 * and Text.Parse.rf).
 *
 * NOTE: Text in RazorForge is UTF-32 encoded. These functions return
 * heap-allocated C strings (ASCII/UTF-8) which is sufficient for numeric formatting
 * since all numeric characters are in the ASCII range. The codegen layer handles
 * conversion to the internal Text representation.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <float.h>
#include <math.h>
#include "types.h"

// Forward declaration from f16_functions.c
extern float rf_f16_to_f32(uint16_t x);
extern uint16_t rf_f16_from_f32(float x);

// ============================================================================
// Formatting: Binary Floating Point
// ============================================================================

rf_address rf_format_F16(rf_U16 value)
{
    char* buffer = (char*)malloc(64);
    if (!buffer) return 0;
    float f = rf_f16_to_f32(value);
    snprintf(buffer, 64, "%.4g", (double)f);
    return (rf_address)buffer;
}

rf_address rf_format_F32(float value)
{
    char* buffer = (char*)malloc(64);
    if (!buffer) return 0;
    snprintf(buffer, 64, "%.7g", (double)value);
    return (rf_address)buffer;
}

rf_address rf_format_F64(double value)
{
    char* buffer = (char*)malloc(64);
    if (!buffer) return 0;
    snprintf(buffer, 64, "%.15g", value);
    return (rf_address)buffer;
}

// F128 formatting is in f128_functions.c (requires LibBF)

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
