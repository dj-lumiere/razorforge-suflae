/*
 * RazorForge Runtime - C# Interop Functions
 *
 * These functions are called by the C# compiler during semantic analysis
 * to parse numeric literals that don't have direct C# equivalents.
 *
 * Types handled:
 * - f128: IEEE binary128 floating point (via LibBF)
 * - d32/d64/d128: IEEE decimal floating point (via Intel DFP)
 * - Integer: Arbitrary precision integer (via LibBF)
 * - Decimal: Arbitrary precision decimal (via MAPM)
 */

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "../include/razorforge_math.h"

// ============================================================================
// f128 string parsing (via LibBF)
// ============================================================================

#ifdef HAVE_LIBBF
#include "libbf.h"

#define F128_PREC 113  // IEEE binary128 mantissa precision

// LibBF context and functions (defined in f128_functions.c)
extern bf_context_t bf_ctx;
extern int bf_ctx_initialized;
extern void ensure_bf_ctx(void);
extern f128_t bf_to_f128(const bf_t *a);

f128_t rf_f128_from_string(const char* str)
{
    ensure_bf_ctx();

    bf_t bf_val;
    bf_init(&bf_ctx, &bf_val);

    // Parse the string using LibBF's arbitrary precision parser
    const char* next;
    int ret = bf_atof(&bf_val, str, &next, 10, F128_PREC, BF_RNDN);

    if (ret != 0 && ret != BF_ST_INEXACT) {
        // Parse error - return NaN
        bf_delete(&bf_val);
        return rf_f128_nan();
    }

    f128_t result = bf_to_f128(&bf_val);
    bf_delete(&bf_val);
    return result;
}

// ============================================================================
// Arbitrary precision integer parsing (via LibBF) - C# Compiler Interop
// These functions use rf_cs_ prefix to distinguish from runtime API
// ============================================================================

// Opaque handle for arbitrary precision integers during compilation
typedef bf_t* rf_cs_integer_t;

rf_cs_integer_t rf_cs_integer_from_string(const char* str)
{
    ensure_bf_ctx();

    bf_t* num = (bf_t*)malloc(sizeof(bf_t));
    if (!num) return NULL;

    bf_init(&bf_ctx, num);

    const char* next;
    int ret = bf_atof(num, str, &next, 10, BF_PREC_INF, BF_RNDZ);

    if (ret != 0) {
        bf_delete(num);
        free(num);
        return NULL;
    }

    // Ensure it's an integer
    bf_rint(num, BF_RNDZ);

    return num;
}

void rf_cs_integer_free(rf_cs_integer_t h)
{
    if (h) {
        bf_delete(h);
        free(h);
    }
}

// Get the size in bytes needed to store the integer as raw limbs
size_t rf_cs_integer_byte_size(rf_cs_integer_t h)
{
    if (!h) return 0;
    return h->len * sizeof(limb_t);
}

// Copy integer limbs to a buffer (for C# to read)
size_t rf_cs_integer_to_bytes(rf_cs_integer_t h, uint8_t* buffer, size_t buffer_size)
{
    if (!h || !buffer) return 0;

    size_t needed = h->len * sizeof(limb_t);
    if (buffer_size < needed) return 0;

    memcpy(buffer, h->tab, needed);
    return needed;
}

// Get the sign (0 = positive, 1 = negative)
int rf_cs_integer_sign(rf_cs_integer_t h)
{
    if (!h) return 0;
    return h->sign;
}

// Get the exponent
int64_t rf_cs_integer_exponent(rf_cs_integer_t h)
{
    if (!h) return 0;
    return h->expn;
}

#endif // HAVE_LIBBF

// ============================================================================
// Arbitrary precision decimal parsing (via MAPM) - C# Compiler Interop
// These functions use rf_cs_ prefix to distinguish from runtime API
// ============================================================================

#ifdef HAVE_MAPM
#include "../mapm/m_apm.h"

// Opaque handle for arbitrary precision decimals during compilation
typedef M_APM rf_cs_decimal_t;

rf_cs_decimal_t rf_cs_decimal_from_string(const char* str)
{
    M_APM num = m_apm_init();
    if (!num) return NULL;

    m_apm_set_string(num, (char*)str);
    return num;
}

void rf_cs_decimal_free(rf_cs_decimal_t h)
{
    if (h) {
        m_apm_free(h);
    }
}

// Get the sign (-1 = negative, 0 = zero, 1 = positive)
int rf_cs_decimal_sign(rf_cs_decimal_t h)
{
    if (!h) return 0;
    return m_apm_sign(h);
}

// Get the exponent (power of 10)
int rf_cs_decimal_exponent(rf_cs_decimal_t h)
{
    if (!h) return 0;
    return m_apm_exponent(h);
}

// Get the number of significant digits
int rf_cs_decimal_significant_digits(rf_cs_decimal_t h)
{
    if (!h) return 0;
    return m_apm_significant_digits(h);
}

// Check if this is an integer value
int rf_cs_decimal_is_integer(rf_cs_decimal_t h)
{
    if (!h) return 0;
    return m_apm_is_integer(h);
}

// Convert to string with specified decimal places
// Caller must free the returned string
char* rf_cs_decimal_to_string(rf_cs_decimal_t h, int decimal_places)
{
    if (!h) return NULL;

    // Allocate enough space for the string
    // max digits = significant_digits + decimal_places + sign + decimal point + null
    int sig_digits = m_apm_significant_digits(h);
    int exp = m_apm_exponent(h);
    size_t buf_size = (size_t)(sig_digits + decimal_places + exp + 10);
    if (buf_size < 64) buf_size = 64;

    char* buffer = (char*)malloc(buf_size);
    if (!buffer) return NULL;

    m_apm_to_fixpt_string(buffer, decimal_places, h);
    return buffer;
}

// Convert to integer string (no decimal point)
// Caller must free the returned string
char* rf_cs_decimal_to_integer_string(rf_cs_decimal_t h)
{
    if (!h) return NULL;

    int sig_digits = m_apm_significant_digits(h);
    int exp = m_apm_exponent(h);
    size_t buf_size = (size_t)(sig_digits + exp + 10);
    if (buf_size < 64) buf_size = 64;

    char* buffer = (char*)malloc(buf_size);
    if (!buffer) return NULL;

    m_apm_to_integer_string(buffer, h);
    return buffer;
}

// Negate the value in place
void rf_cs_decimal_negate(rf_cs_decimal_t h)
{
    if (h) {
        m_apm_negate(h, h);
    }
}

#endif // HAVE_MAPM

// ============================================================================
// Decimal floating point string parsing (Intel DFP)
// These are wrappers around functions in decimal_functions.c
// ============================================================================

// Already provided by decimal_functions.c:
// - d32_from_string(const char* str)
// - d64_from_string(const char* str)
// - d128_from_string(const char* str)
