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

// ============================================================================
// Text Concatenation
// ============================================================================

/**
 * Concatenates two Text entities, returning a new Text entity.
 *
 * Text layout:   { ptr characters }       — pointer to List[Character]
 * List layout:   { ptr data, i64 count, i64 capacity }
 * Character:        i32 (UTF-32 codepoint)
 */

// Forward declaration from memory.c
extern void* rf_allocate_dynamic(uint64_t bytes);

typedef struct {
    void* data;
    int64_t count;
    int64_t capacity;
} rf_List;

typedef struct {
    rf_List* characters;
} rf_Text;

/**
 * Formats a pointer as "0xXXXX_XXXX_XXXX_XXXX" and returns a Text entity.
 * Used by RF entity __diagnose__() to include the heap address.
 */
void* rf_format_address(void* ptr)
{
    uint64_t addr = (uint64_t)ptr;

    // Format: "0xXXXX_XXXX_XXXX_XXXX" = 2 + 16 + 3 = 21 characters
    const int len = 21;
    int32_t* data = (int32_t*)rf_allocate_dynamic((uint64_t)len * sizeof(int32_t));

    // "0x" prefix
    data[0] = '0';
    data[1] = 'x';

    // 16 hex digits in groups of 4, separated by underscores
    const char hex[] = "0123456789ABCDEF";
    int pos = 2;
    for (int group = 0; group < 4; group++) {
        if (group > 0) {
            data[pos++] = '_';
        }
        int shift = (3 - group) * 16;
        for (int d = 0; d < 4; d++) {
            int nibble = (int)((addr >> (shift + (3 - d) * 4)) & 0xF);
            data[pos++] = hex[nibble];
        }
    }

    rf_List* list = (rf_List*)rf_allocate_dynamic(sizeof(rf_List));
    list->data = data;
    list->count = len;
    list->capacity = len;

    rf_Text* text = (rf_Text*)rf_allocate_dynamic(sizeof(rf_Text));
    text->characters = list;

    return text;
}

void* rf_text_concat(void* text_a, void* text_b)
{
    rf_Text* a = (rf_Text*)text_a;
    rf_Text* b = (rf_Text*)text_b;

    int64_t count_a = a->characters->count;
    int64_t count_b = b->characters->count;
    int64_t total = count_a + count_b;

    // Allocate new data array (i32 per codepoint)
    int32_t* new_data = (int32_t*)rf_allocate_dynamic((uint64_t)total * sizeof(int32_t));
    if (count_a > 0)
        memcpy(new_data, a->characters->data, (size_t)count_a * sizeof(int32_t));
    if (count_b > 0)
        memcpy(new_data + count_a, b->characters->data, (size_t)count_b * sizeof(int32_t));

    // Allocate new List[Character] struct
    rf_List* new_list = (rf_List*)rf_allocate_dynamic(sizeof(rf_List));
    new_list->data = new_data;
    new_list->count = total;
    new_list->capacity = total;

    // Allocate new Text wrapper
    rf_Text* new_text = (rf_Text*)rf_allocate_dynamic(sizeof(rf_Text));
    new_text->characters = new_list;

    return new_text;
}
