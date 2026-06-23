/*
 * RazorForge Runtime - f128 (Quad Precision) Floating Point Functions
 *
 * Math (arithmetic, comparisons, conversions, transcendentals) is backed by
 * TLFloat: correctly-rounded IEEE binary128 soft-float with a full
 * libm-equivalent function set, bit-identical on every platform.
 *
 * String parse/format stays on LibBF: its FREE_MIN decimal style (shortest
 * exact expansion capped at 12 significant digits, fixed/exponential switch)
 * is locked in by the fixture snapshots, and LibBF remains in the build for
 * the bignum compile-time bridges regardless.
 */

#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include "../include/razorforge_math.h"

// NOTE: TLFloat retired. F128 arithmetic/libm/conversions are now pure-RF (SoftFloat/F128*.rf);
// this file keeps only the LibBF context + f128<->bf bridges used by rf_f128_from_string
// (compile-time literal fallback) / rf_f128_to_string and the trivial special-value constructors.
#include "libbf.h"

// IEEE binary128 format:
// - 1 bit sign
// - 15 bits exponent (bias 16383)
// - 112 bits mantissa (+ 1 implicit bit = 113 bits precision)
#define F128_MANT_BITS 112
#define F128_EXP_BITS 15
#define F128_EXP_BIAS 16383
#define F128_PREC 113  // mantissa bits including implicit bit

// 128-bit integer types for LLVM i128 compatibility
// Layout matches LLVM's i128 ABI on x86_64/AArch64: low word first
typedef struct { uint64_t low; uint64_t high; } u128_t;
typedef struct { uint64_t low; int64_t high; } s128_t;

// Forward declarations for functions used before definition

// Global LibBF context (non-static for use by csharp_interop.c)
bf_context_t bf_ctx;
int bf_ctx_initialized = 0;

static void *rf_bf_realloc(void *opaque, void *ptr, size_t size)
{
    (void)opaque;
    if (size == 0) {
        free(ptr);
        return NULL;
    }
    return realloc(ptr, size);
}

void ensure_bf_ctx(void)
{
    if (!bf_ctx_initialized) {
        bf_context_init(&bf_ctx, rf_bf_realloc, NULL);
        bf_ctx_initialized = 1;
    }
}

// Convert f128_t to bf_t
static void f128_to_bf(bf_t *r, f128_t x)
{
    ensure_bf_ctx();
    bf_init(&bf_ctx, r);

    // Extract sign, exponent, mantissa from IEEE binary128
    int sign = (x.high >> 63) & 1;
    int exp = (x.high >> 48) & 0x7FFF;
    uint64_t mant_high = x.high & 0x0000FFFFFFFFFFFFULL;  // 48 bits
    uint64_t mant_low = x.low;  // 64 bits

    if (exp == 0x7FFF) {
        // Infinity or NaN
        if (mant_high == 0 && mant_low == 0) {
            bf_set_inf(r, sign);
        } else {
            bf_set_nan(r);
        }
        return;
    }

    if (exp == 0 && mant_high == 0 && mant_low == 0) {
        // Zero
        bf_set_zero(r, sign);
        return;
    }

    // Subnormal numbers (exp == 0 but mantissa != 0)
    if (exp == 0) {
        // Subnormal - very small, approximate as 0
        bf_set_zero(r, sign);
        return;
    }

    // For normal numbers, we need to properly construct the bf_t
    // The mantissa is 112 bits, we'll construct it from the raw bits

    // Set up the value: (-1)^sign * 2^(exp - bias) * 1.mantissa
    bf_set_ui(r, 1);  // Start with 1 (implicit bit)

    // Add mantissa bits
    // mantissa = mant_high (48 bits) << 64 | mant_low (64 bits)
    // Total 112 bits after the binary point

    bf_t mant_bf, two_bf, temp;
    bf_init(&bf_ctx, &mant_bf);
    bf_init(&bf_ctx, &two_bf);
    bf_init(&bf_ctx, &temp);

    // Build mantissa: 1 + (mant_high * 2^64 + mant_low) / 2^112
    bf_set_ui(&mant_bf, mant_high);
    bf_set_ui(&two_bf, 1);
    bf_mul_2exp(&mant_bf, 64, F128_PREC, BF_RNDN);  // mant_high << 64

    bf_set_ui(&temp, mant_low);
    bf_add(&mant_bf, &mant_bf, &temp, F128_PREC, BF_RNDN);  // + mant_low

    // Divide by 2^112 to get fractional part
    bf_mul_2exp(&mant_bf, -F128_MANT_BITS, F128_PREC, BF_RNDN);

    // Add 1 for implicit bit
    bf_set_ui(r, 1);
    bf_add(r, r, &mant_bf, F128_PREC, BF_RNDN);

    // Multiply by 2^(exp - bias)
    bf_mul_2exp(r, exp - F128_EXP_BIAS, F128_PREC, BF_RNDN);

    // Set sign
    r->sign = sign;

    bf_delete(&mant_bf);
    bf_delete(&two_bf);
    bf_delete(&temp);
}

// Convert bf_t to f128_t (non-static for use by csharp_interop.c)
// Direct bit extraction from LibBF - no precision loss
f128_t bf_to_f128(const bf_t *a)
{
    f128_t result = {0, 0};

    // Handle special cases
    if (bf_is_nan(a)) {
        result.high = 0x7FFF800000000000ULL;  // Quiet NaN
        return result;
    }

    if (bf_is_zero(a)) {
        if (a->sign) result.high = 0x8000000000000000ULL;
        return result;
    }

    if (a->expn == BF_EXP_INF) {
        result.high = a->sign ? 0xFFFF000000000000ULL : 0x7FFF000000000000ULL;
        return result;
    }

    // Round to f128 precision to ensure exactly 113 bits
    bf_t rounded;
    bf_init(&bf_ctx, &rounded);
    bf_set(&rounded, a);
    bf_round(&rounded, F128_PREC, BF_RNDN);

    // Calculate IEEE biased exponent
    // LibBF: value = mantissa * 2^(expn - len*64) where mantissa MSB is at bit (len*64-1)
    // For normalized bf_t with len=2: value = m * 2^(expn - 128) where m in [2^127, 2^128)
    // IEEE: value = 1.fraction * 2^(exp - bias)
    // Therefore: exp = expn + bias - 1 = expn + 16382
    slimb_t ieee_exp = rounded.expn + (F128_EXP_BIAS - 1);

    // Handle overflow (exponent too large for f128)
    if (ieee_exp >= 0x7FFF) {
        bf_delete(&rounded);
        result.high = a->sign ? 0xFFFF000000000000ULL : 0x7FFF000000000000ULL;
        return result;
    }

    // Handle underflow (flush to zero - subnormals not implemented)
    if (ieee_exp <= 0) {
        bf_delete(&rounded);
        if (a->sign) result.high = 0x8000000000000000ULL;
        return result;
    }

    // Extract mantissa bits directly from LibBF's internal representation
    // After bf_round to 113 bits, rounded.len should be 2 (128 bits total)
    //
    // LibBF layout with len=2:
    //   tab[1]: bits 127-64 of 128-bit representation
    //   tab[0]: bits 63-0 of 128-bit representation
    //   113-bit mantissa is in bits 127-15 (bit 127 is implicit 1)
    //
    // IEEE binary128 layout:
    //   high: [sign:1][exp:15][mant_high:48]
    //   low:  [mant_low:64]
    //   112 explicit mantissa bits (implicit 1 not stored)
    //
    // Mapping:
    //   mant_high (48 bits) = LibBF bits 126-79 = tab[1] bits 62-15
    //   mant_low (64 bits)  = LibBF bits 78-15  = tab[1] bits 14-0 + tab[0] bits 63-15

    uint64_t tab0 = (rounded.len > 0) ? rounded.tab[0] : 0;
    uint64_t tab1 = (rounded.len > 1) ? rounded.tab[1] : 0;
    uint64_t mant_high = 0, mant_low = 0;

    if (rounded.len >= 2) {
        // Standard case: 2 limbs (113-bit precision)
        // Extract bits 62-15 of tab[1] for mant_high (48 bits)
        mant_high = (tab1 >> 15) & 0xFFFFFFFFFFFFULL;
        // Extract bits 14-0 of tab[1] and bits 63-15 of tab[0] for mant_low (64 bits)
        mant_low = ((tab1 & 0x7FFFULL) << 49) | (tab0 >> 15);
    } else if (rounded.len == 1) {
        // Single limb case (precision < 64 bits, unusual but handle it)
        // Bit 63 is implicit 1, bits 62-15 go to mant_high
        mant_high = (tab0 >> 15) & 0xFFFFFFFFFFFFULL;
        mant_low = (tab0 & 0x7FFFULL) << 49;
    }

    // Assemble the f128 result
    result.high = ((uint64_t)rounded.sign << 63) |
                  ((uint64_t)ieee_exp << 48) |
                  mant_high;
    result.low = mant_low;

    bf_delete(&rounded);
    return result;
}

// Convert f128 to decimal string (caller must free)
char* rf_f128_to_string(f128_t x)
{
    ensure_bf_ctx();
    bf_t bx;
    f128_to_bf(&bx, x);

    char *buf;
    size_t len;
    // Use bf_ftoa with 36 significant digits (enough for f128's ~34 digits)
    buf = bf_ftoa(&len, &bx, 10, 36, BF_FTOA_FORMAT_FREE_MIN | BF_RNDN);

    bf_delete(&bx);

    if (!buf) {
        buf = (char*)malloc(4);
        strcpy(buf, "NaN");
    }
    return buf;
}

// ============================================================================
// Special values
// ============================================================================

f128_t rf_f128_nan(void)
{
    f128_t r = {0, 0x7FFF800000000000ULL};
    return r;
}

f128_t rf_f128_inf(void)
{
    f128_t r = {0, 0x7FFF000000000000ULL};  // Positive infinity
    return r;
}

f128_t rf_f128_neg_inf(void)
{
    f128_t r = {0, 0xFFFF000000000000ULL};  // Negative infinity
    return r;
}

f128_t rf_f128_zero(int negative)
{
    f128_t r = {0, negative ? 0x8000000000000000ULL : 0};
    return r;
}

