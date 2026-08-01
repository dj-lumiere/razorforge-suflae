/*
 * RazorForge Runtime - C# Interop Functions
 *
 * These functions are called by the C# compiler during semantic analysis
 * to parse numeric literals that don't have direct C# equivalents.
 *
 * Types handled:
 * - f128: IEEE binary128 floating point (via LibBF)
 * - Integer: Arbitrary precision integer (via LibBF)
 *
 * (Decimal literal parsing now lives in the managed BID encoders in
 *  src/Verification/NumericLiteralParser.cs; the decNumber-backed
 *  rf_cs_decimal_* FFI it replaced has been removed.)
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
