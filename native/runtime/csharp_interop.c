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
// Arbitrary precision decimal parsing (via decNumber) — C# Compiler Interop
// Implements the rf_cs_decimal_* symbols the C# NumericLiteralParser P/Invokes
// (src/Verification/NumericLiteralParser.cs) to parse `dn` Decimal literals at
// compile time. Backed by decNumber (HAVE_DECNUMBER); a high DECNUMDIGITS lets
// arbitrary-precision literals parse without rounding. The handle is an opaque
// heap decNumber, freed via rf_cs_decimal_free.
// ============================================================================

#ifdef HAVE_DECNUMBER
#ifndef DECNUMDIGITS
#define DECNUMDIGITS 1000
#endif
#include <decContext.h>
#include <decNumber.h>

typedef decNumber* rf_cs_decimal_t;

// Per-thread parsing context: arbitrary precision, no traps (errors via status).
static decContext* rf_cs_dec_ctx(void)
{
    static _Thread_local decContext ctx;
    static _Thread_local int inited = 0;
    if (!inited) {
        decContextDefault(&ctx, DEC_INIT_BASE);
        ctx.digits = DECNUMDIGITS;
        ctx.emax = 999999999;
        ctx.emin = -999999999;
        ctx.round = DEC_ROUND_HALF_EVEN;
        ctx.traps = 0;
        inited = 1;
    }
    return &ctx;
}

rf_cs_decimal_t rf_cs_decimal_from_string(const char* str)
{
    decNumber* num = (decNumber*)malloc(sizeof(decNumber));
    if (!num) return NULL;
    decContext* ctx = rf_cs_dec_ctx();
    ctx->status = 0;
    decNumberFromString(num, str, ctx);
    if (ctx->status & DEC_Errors) {
        free(num);
        return NULL;
    }
    return num;
}

void rf_cs_decimal_free(rf_cs_decimal_t h)
{
    if (h) free(h);
}

// Sign: -1 negative, 0 zero, +1 positive.
int rf_cs_decimal_sign(rf_cs_decimal_t h)
{
    if (!h) return 0;
    if (decNumberIsZero(h)) return 0;
    return decNumberIsNegative(h) ? -1 : 1;
}

// Power-of-ten exponent of the coefficient.
int rf_cs_decimal_exponent(rf_cs_decimal_t h)
{
    return h ? h->exponent : 0;
}

// Count of significant digits in the coefficient.
int rf_cs_decimal_significant_digits(rf_cs_decimal_t h)
{
    return h ? h->digits : 0;
}

// Integer iff there is no fractional part (exponent >= 0).
int rf_cs_decimal_is_integer(rf_cs_decimal_t h)
{
    return (h && h->exponent >= 0) ? 1 : 0;
}

// Negate in place by toggling the sign bit (no-op for zero; no context needed).
void rf_cs_decimal_negate(rf_cs_decimal_t h)
{
    if (h && !decNumberIsZero(h)) h->bits ^= DECNEG;
}

// Canonical decimal string. Caller (C#) reads then leaks it (matches the
// DecimalToString contract in NumericLiteralParser.cs). decimal_places is
// ignored; decNumberToString emits full precision.
char* rf_cs_decimal_to_string(rf_cs_decimal_t h, int decimal_places)
{
    (void)decimal_places;
    if (!h) return NULL;
    char* buf = (char*)malloc((size_t)h->digits + 14); // decNumber spec: digits+14
    if (!buf) return NULL;
    decNumberToString(h, buf);
    return buf;
}

// Integer-valued string (fractional part truncated toward zero).
char* rf_cs_decimal_to_integer_string(rf_cs_decimal_t h)
{
    if (!h) return NULL;
    decContext* ctx = rf_cs_dec_ctx();
    decNumber tmp;
    decNumberToIntegralValue(&tmp, h, ctx);
    char* buf = (char*)malloc((size_t)tmp.digits + 14);
    if (!buf) return NULL;
    decNumberToString(&tmp, buf);
    return buf;
}
#endif // HAVE_DECNUMBER

// ============================================================================
// Decimal floating point string parsing (decNumber)
// These are wrappers around functions in decimal_functions.c
// ============================================================================

// Already provided by decimal_functions.c:
// - d32_from_string(const char* str)
// - d64_from_string(const char* str)
// - d128_from_string(const char* str)
