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

#ifndef HAVE_TLFLOAT
#error "TLFloat is required for f128 math. Clone it into native/tlfloat (see native/cmake/tlfloat.cmake)."
#endif
#include <tlfloat/tlfloat.h>

// f128_t <-> tlfloat_quad carry the same IEEE binary128 bits; tlfloat_quad is
// either a native __float128 / float128-long-double or a {uint64_t e[2]}
// struct depending on platform, so convert through memcpy, never by cast.
static inline tlfloat_quad_ f128_to_q(f128_t x)
{
    tlfloat_quad_ q;
    memcpy(&q, &x, 16);
    return q;
}

static inline f128_t q_to_f128(tlfloat_quad_ q)
{
    f128_t x;
    memcpy(&x, &q, 16);
    return x;
}

#ifdef HAVE_LIBBF
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
static f128_t rf_f128_zero(int negative);
f128_t rf_f128_abs(f128_t a);  // exported in header
f128_t rf_f64_to_f128(double x);
f128_t rf_s64_to_f128(int64_t x);
f128_t rf_u64_to_f128(uint64_t x);

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
// Conversion from other float types
// ============================================================================

f128_t rf_f32_to_f128(float x)
{
    return rf_f64_to_f128((double)x);
}

f128_t rf_f64_to_f128(double x)
{
    // Exact widening, including f64 subnormals (the previous hand-rolled
    // converter flushed them to zero).
    return q_to_f128(tlfloat_cast_q_d_(x));
}

float rf_f128_to_f32(f128_t x)
{
    // Via f64. The intermediate rounding can in principle double-round, but
    // only for values within half an f64-ulp of an f32 tie — and the previous
    // truncating converter was strictly less accurate.
    return (float)tlfloat_cast_d_q(f128_to_q(x));
}

double rf_f128_to_f64(f128_t x)
{
    // Correctly rounded narrowing with subnormal results (the previous
    // converter truncated the mantissa and flushed subnormals to zero).
    return tlfloat_cast_d_q(f128_to_q(x));
}

// ============================================================================
// Conversion from/to integers
// ============================================================================

f128_t rf_s32_to_f128(int32_t x)
{
    return q_to_f128(tlfloat_cast_q_i64_((int64_t)x));
}

f128_t rf_s64_to_f128(int64_t x)
{
    return q_to_f128(tlfloat_cast_q_i64_(x));
}

f128_t rf_u32_to_f128(uint32_t x)
{
    return q_to_f128(tlfloat_cast_q_u64_((uint64_t)x));
}

f128_t rf_u64_to_f128(uint64_t x)
{
    return q_to_f128(tlfloat_cast_q_u64_(x));
}

// 128-bit integer to f128 conversions. Values above 2^113 round to nearest
// (the previous hand-rolled converters truncated the lost bits instead).

f128_t rf_u128_to_f128(u128_t x)
{
    tlfloat_uint128_t_ u;
    memcpy(&u, &x, 16);
    return q_to_f128(tlfloat_cast_q_u128(u));
}

f128_t rf_s128_to_f128(s128_t x)
{
    tlfloat_int128_t_ s;
    memcpy(&s, &x, 16);
    return q_to_f128(tlfloat_cast_q_i128(s));
}

int32_t rf_f128_to_s32(f128_t x)
{
    int64_t val = rf_f128_to_s64(x);
    if (val > INT32_MAX) return INT32_MAX;
    if (val < INT32_MIN) return INT32_MIN;
    return (int32_t)val;
}

int64_t rf_f128_to_s64(f128_t x)
{
    int sign = (x.high >> 63) & 1;
    int exp = (x.high >> 48) & 0x7FFF;
    uint64_t mant_high = x.high & 0x0000FFFFFFFFFFFFULL;
    uint64_t mant_low = x.low;

    // Handle special cases
    if (exp == 0x7FFF) {
        // Infinity or NaN -> clamp to max/min
        return sign ? INT64_MIN : INT64_MAX;
    }

    if (exp == 0) {
        // Zero or subnormal
        return 0;
    }

    // True exponent (unbiased)
    int true_exp = exp - F128_EXP_BIAS;

    // If exponent is negative, value is < 1, truncates to 0
    if (true_exp < 0) return 0;

    // If exponent >= 63, value is too large for int64_t
    if (true_exp >= 63) {
        return sign ? INT64_MIN : INT64_MAX;
    }

    // Reconstruct the integer value
    // The mantissa represents 1.mant_high:mant_low in binary
    // We have 112 mantissa bits + implicit 1 = 113 bits total precision

    // Start with implicit 1
    uint64_t result;

    if (true_exp <= 48) {
        // Result fits using just mant_high
        // We need (true_exp) bits from mantissa + the implicit 1
        result = (1ULL << true_exp) | (mant_high >> (48 - true_exp));
    } else {
        // Need bits from both mant_high and mant_low
        int bits_from_low = true_exp - 48;
        result = (1ULL << true_exp) | (mant_high << bits_from_low) | (mant_low >> (64 - bits_from_low));
    }

    return sign ? -(int64_t)result : (int64_t)result;
}

uint32_t rf_f128_to_u32(f128_t x)
{
    uint64_t val = rf_f128_to_u64(x);
    if (val > UINT32_MAX) return UINT32_MAX;
    return (uint32_t)val;
}

uint64_t rf_f128_to_u64(f128_t x)
{
    int sign = (x.high >> 63) & 1;
    int exp = (x.high >> 48) & 0x7FFF;
    uint64_t mant_high = x.high & 0x0000FFFFFFFFFFFFULL;
    uint64_t mant_low = x.low;

    // Negative values -> 0 for unsigned
    if (sign) return 0;

    // Handle special cases
    if (exp == 0x7FFF) {
        // Infinity or NaN -> max value
        return UINT64_MAX;
    }

    if (exp == 0) {
        // Zero or subnormal
        return 0;
    }

    // True exponent
    int true_exp = exp - F128_EXP_BIAS;

    if (true_exp < 0) return 0;
    if (true_exp >= 64) return UINT64_MAX;

    // Reconstruct the integer
    uint64_t result;

    if (true_exp <= 48) {
        result = (1ULL << true_exp) | (mant_high >> (48 - true_exp));
    } else {
        int bits_from_low = true_exp - 48;
        result = (1ULL << true_exp) | (mant_high << bits_from_low) | (mant_low >> (64 - bits_from_low));
    }

    return result;
}

// f128 to 128-bit integer conversions

u128_t rf_f128_to_u128(f128_t x)
{
    u128_t result = {0, 0};

    int sign = (x.high >> 63) & 1;
    int exp = (x.high >> 48) & 0x7FFF;
    uint64_t mant_high = x.high & 0x0000FFFFFFFFFFFFULL;
    uint64_t mant_low = x.low;

    // Negative values -> 0 for unsigned
    if (sign) return result;

    // Handle special cases
    if (exp == 0x7FFF) {
        // Infinity or NaN -> max value
        result.low = UINT64_MAX;
        result.high = UINT64_MAX;
        return result;
    }

    if (exp == 0) {
        // Zero or subnormal
        return result;
    }

    // True exponent
    int true_exp = exp - F128_EXP_BIAS;

    if (true_exp < 0) return result;  // Value < 1, truncates to 0

    if (true_exp >= 128) {
        // Overflow
        result.low = UINT64_MAX;
        result.high = UINT64_MAX;
        return result;
    }

    // Reconstruct the integer from mantissa
    // We have: implicit 1 (at bit true_exp) + 112 mantissa bits below it
    // mant_high: 48 bits, mant_low: 64 bits

    if (true_exp <= 48) {
        // Result fits in low word using just mant_high
        result.low = (1ULL << true_exp) | (mant_high >> (48 - true_exp));
        result.high = 0;
    } else if (true_exp <= 64) {
        // Result fits in low word, needs both mant_high and some mant_low
        int bits_from_low = true_exp - 48;
        result.low = (1ULL << true_exp) | (mant_high << bits_from_low) | (mant_low >> (64 - bits_from_low));
        result.high = 0;
    } else if (true_exp <= 112) {
        // Result spans both words
        int bits_in_high = true_exp - 64;
        // High word: implicit 1 at position (true_exp - 64), plus mantissa bits
        if (bits_in_high <= 48) {
            result.high = (1ULL << bits_in_high) | (mant_high >> (48 - bits_in_high));
            // Low word: remaining mant_high bits + mant_low bits
            int mant_high_bits_in_low = 48 - bits_in_high;
            result.low = (mant_high << (64 - mant_high_bits_in_low)) | (mant_low >> mant_high_bits_in_low);
        } else {
            // bits_in_high > 48, some mant_low goes into high word
            int mant_low_bits_in_high = bits_in_high - 48;
            result.high = (1ULL << bits_in_high) | (mant_high << mant_low_bits_in_high) | (mant_low >> (64 - mant_low_bits_in_high));
            result.low = mant_low << mant_low_bits_in_high;
        }
    } else {
        // true_exp > 112: integer is larger than mantissa precision
        // Shift the mantissa to the right position
        int shift = true_exp - 112;
        if (shift < 64) {
            // mant_high and mant_low form the 112-bit mantissa
            // Add implicit 1 and shift left by 'shift'
            result.low = mant_low << shift;
            result.high = (mant_high << shift) | (mant_low >> (64 - shift));
            // Add the implicit 1 at position true_exp
            if (true_exp >= 64) {
                result.high |= (1ULL << (true_exp - 64));
            }
        } else {
            // shift >= 64
            result.low = 0;
            result.high = mant_low << (shift - 64);
            // Add implicit 1
            result.high |= (1ULL << (true_exp - 64));
        }
    }

    return result;
}

s128_t rf_f128_to_s128(f128_t x)
{
    s128_t result = {0, 0};

    int sign = (x.high >> 63) & 1;
    int exp = (x.high >> 48) & 0x7FFF;
    uint64_t mant_high = x.high & 0x0000FFFFFFFFFFFFULL;
    uint64_t mant_low = x.low;

    // Handle special cases
    if (exp == 0x7FFF) {
        // Infinity or NaN -> clamp to max/min
        if (sign) {
            result.low = 0;
            result.high = (int64_t)0x8000000000000000LL;  // INT128_MIN
        } else {
            result.low = UINT64_MAX;
            result.high = 0x7FFFFFFFFFFFFFFFLL;  // INT128_MAX
        }
        return result;
    }

    if (exp == 0) {
        // Zero or subnormal
        return result;
    }

    // True exponent
    int true_exp = exp - F128_EXP_BIAS;

    if (true_exp < 0) return result;  // Value < 1, truncates to 0

    if (true_exp >= 127) {
        // Overflow (s128 max is 2^127 - 1)
        if (sign) {
            result.low = 0;
            result.high = (int64_t)0x8000000000000000LL;  // INT128_MIN
        } else {
            result.low = UINT64_MAX;
            result.high = 0x7FFFFFFFFFFFFFFFLL;  // INT128_MAX
        }
        return result;
    }

    // Get the unsigned value first
    u128_t unsigned_val = rf_f128_to_u128(rf_f128_abs(x));

    if (sign) {
        // Negate: ~x + 1
        result.low = ~unsigned_val.low + 1;
        result.high = ~(int64_t)unsigned_val.high + (result.low == 0 ? 1 : 0);
    } else {
        result.low = unsigned_val.low;
        result.high = (int64_t)unsigned_val.high;
    }

    return result;
}

// ============================================================================
// Basic arithmetic (TLFloat, correctly rounded)
// ============================================================================

f128_t rf_f128_add(f128_t a, f128_t b)
{
    return q_to_f128(tlfloat_addq(f128_to_q(a), f128_to_q(b)));
}

f128_t rf_f128_sub(f128_t a, f128_t b)
{
    return q_to_f128(tlfloat_subq(f128_to_q(a), f128_to_q(b)));
}

f128_t rf_f128_mul(f128_t a, f128_t b)
{
    return q_to_f128(tlfloat_mulq(f128_to_q(a), f128_to_q(b)));
}

f128_t rf_f128_div(f128_t a, f128_t b)
{
    return q_to_f128(tlfloat_divq(f128_to_q(a), f128_to_q(b)));
}

f128_t rf_f128_sqrt(f128_t a)
{
    return q_to_f128(tlfloat_sqrtq(f128_to_q(a)));
}

f128_t rf_f128_neg(f128_t a)
{
    a.high ^= 0x8000000000000000ULL;
    return a;
}

f128_t rf_f128_abs(f128_t a)
{
    a.high &= 0x7FFFFFFFFFFFFFFFULL;
    return a;
}

// ============================================================================
// Comparisons
// ============================================================================

int rf_f128_eq(f128_t a, f128_t b)
{
    return tlfloat_eq_q_q(f128_to_q(a), f128_to_q(b));
}

int rf_f128_lt(f128_t a, f128_t b)
{
    return tlfloat_lt_q_q(f128_to_q(a), f128_to_q(b));
}

int rf_f128_le(f128_t a, f128_t b)
{
    return tlfloat_le_q_q(f128_to_q(a), f128_to_q(b));
}

int rf_f128_gt(f128_t a, f128_t b) { return rf_f128_lt(b, a); }
int rf_f128_ge(f128_t a, f128_t b) { return rf_f128_le(b, a); }

// ============================================================================
// Classification
// ============================================================================

int rf_f128_is_nan(f128_t x)
{
    int exp = (x.high >> 48) & 0x7FFF;
    uint64_t mant_high = x.high & 0x0000FFFFFFFFFFFFULL;
    return (exp == 0x7FFF) && (mant_high != 0 || x.low != 0);
}

int rf_f128_is_inf(f128_t x)
{
    int exp = (x.high >> 48) & 0x7FFF;
    uint64_t mant_high = x.high & 0x0000FFFFFFFFFFFFULL;
    return (exp == 0x7FFF) && (mant_high == 0 && x.low == 0);
}

int rf_f128_is_zero(f128_t x)
{
    return ((x.high & 0x7FFFFFFFFFFFFFFFULL) == 0) && (x.low == 0);
}

int rf_f128_is_negative(f128_t x)
{
    return (x.high >> 63) & 1;
}

int rf_f128_is_finite(f128_t x)
{
    int exp = (x.high >> 48) & 0x7FFF;
    return exp != 0x7FFF;
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

// ============================================================================
// Transcendental functions (TLFloat, correctly rounded at quad precision)
// ============================================================================

f128_t rf_f128_sin(f128_t x)  { return q_to_f128(tlfloat_sinq(f128_to_q(x))); }
f128_t rf_f128_cos(f128_t x)  { return q_to_f128(tlfloat_cosq(f128_to_q(x))); }
f128_t rf_f128_tan(f128_t x)  { return q_to_f128(tlfloat_tanq(f128_to_q(x))); }
f128_t rf_f128_asin(f128_t x) { return q_to_f128(tlfloat_asinq(f128_to_q(x))); }
f128_t rf_f128_acos(f128_t x) { return q_to_f128(tlfloat_acosq(f128_to_q(x))); }
f128_t rf_f128_atan(f128_t x) { return q_to_f128(tlfloat_atanq(f128_to_q(x))); }

f128_t rf_f128_atan2(f128_t y, f128_t x)
{
    return q_to_f128(tlfloat_atan2q(f128_to_q(y), f128_to_q(x)));
}

f128_t rf_f128_exp(f128_t x) { return q_to_f128(tlfloat_expq(f128_to_q(x))); }
f128_t rf_f128_log(f128_t x) { return q_to_f128(tlfloat_logq(f128_to_q(x))); }

f128_t rf_f128_pow(f128_t x, f128_t y)
{
    return q_to_f128(tlfloat_powq(f128_to_q(x), f128_to_q(y)));
}

// All previously derived via exp/log identities (with the associated
// precision loss near 0 / over- and underflow at the range edges); TLFloat
// implements each one natively.

f128_t rf_f128_sinh(f128_t x)   { return q_to_f128(tlfloat_sinhq(f128_to_q(x))); }
f128_t rf_f128_cosh(f128_t x)   { return q_to_f128(tlfloat_coshq(f128_to_q(x))); }
f128_t rf_f128_tanh(f128_t x)   { return q_to_f128(tlfloat_tanhq(f128_to_q(x))); }
f128_t rf_f128_asinh(f128_t x)  { return q_to_f128(tlfloat_asinhq(f128_to_q(x))); }
f128_t rf_f128_acosh(f128_t x)  { return q_to_f128(tlfloat_acoshq(f128_to_q(x))); }
f128_t rf_f128_atanh(f128_t x)  { return q_to_f128(tlfloat_atanhq(f128_to_q(x))); }
f128_t rf_f128_log2(f128_t x)   { return q_to_f128(tlfloat_log2q(f128_to_q(x))); }
f128_t rf_f128_log10(f128_t x)  { return q_to_f128(tlfloat_log10q(f128_to_q(x))); }
f128_t rf_f128_exp2(f128_t x)   { return q_to_f128(tlfloat_exp2q(f128_to_q(x))); }
f128_t rf_f128_expm1(f128_t x)  { return q_to_f128(tlfloat_expm1q(f128_to_q(x))); }
f128_t rf_f128_log1p(f128_t x)  { return q_to_f128(tlfloat_log1pq(f128_to_q(x))); }
f128_t rf_f128_cbrt(f128_t x)   { return q_to_f128(tlfloat_cbrtq(f128_to_q(x))); }

f128_t rf_f128_hypot(f128_t x, f128_t y)
{
    return q_to_f128(tlfloat_hypotq(f128_to_q(x), f128_to_q(y)));
}

f128_t rf_f128_fmod(f128_t x, f128_t y)
{
    return q_to_f128(tlfloat_fmodq(f128_to_q(x), f128_to_q(y)));
}

// ============================================================================
// Rounding
// ============================================================================

f128_t rf_f128_floor(f128_t x) { return q_to_f128(tlfloat_floorq(f128_to_q(x))); }
f128_t rf_f128_ceil(f128_t x)  { return q_to_f128(tlfloat_ceilq(f128_to_q(x))); }
f128_t rf_f128_trunc(f128_t x) { return q_to_f128(tlfloat_truncq(f128_to_q(x))); }

f128_t rf_f128_round(f128_t x)
{
    // Round to nearest, ties away from zero (C round semantics, matching the
    // previous BF_RNDNA behavior).
    return q_to_f128(tlfloat_roundq(f128_to_q(x)));
}

// ============================================================================
// REMOVED: __addtf3-family fp128 soft-float builtin shims (Win64 + Apple).
//
// RazorForge codegen no longer emits LLVM fp128 instructions anywhere — F128
// is an i128 bit carrier and every operation calls an rf_f128_*_parts bridge
// directly — so no compiler-rt __*tf* libcall can be generated and the
// platform-specific ABI shims that used to live here are gone.
// ============================================================================



// ============================================================================
// String parsing
// ============================================================================

// Parse a null-terminated decimal string (CStr) into an F128 value.
// Uses LibBF's bf_atof for full 113-bit precision.
//
// Out-param ABI: the result is written through `out` rather than returned by value.
// Windows x64 returns 16-byte aggregates via a hidden sret pointer in rcx, but the
// RazorForge caller compiles the call as if it returns an fp128 in xmm0 — the two
// ABIs are incompatible and the function would never effectively execute. Writing
// through a pointer sidesteps the mismatch.
void rf_parse_F128(const char* cstr, f128_t* out)
{
    if (!cstr || *cstr == '\0') {
        *out = rf_f128_zero(0);
        return;
    }
    ensure_bf_ctx();
    bf_t r;
    bf_init(&bf_ctx, &r);
    const char *next = NULL;
    bf_atof(&r, cstr, &next, 10, F128_PREC, BF_RNDN);
    *out = bf_to_f128(&r);
    bf_delete(&r);
}

// ============================================================================
// String formatting
// ============================================================================

uint64_t rf_format_F128(f128_t x)
{
    // Canonical specials — "NaN" (always unsigned) / "inf" / "-inf" — matching
    // F16/F32/F64 and the decimal formats. Checked on the raw bits so LibBF's
    // own spellings ("NaN"/"Inf"/"-Inf") never surface.
    uint32_t sp_exp = (uint32_t)((x.high >> 48) & 0x7FFF);
    uint64_t sp_mant = (x.high & 0x0000FFFFFFFFFFFFULL) | x.low;
    if (sp_exp == 0x7FFF)
    {
        const char* s = sp_mant != 0 ? "NaN" : ((x.high >> 63) ? "-inf" : "inf");
        char* sbuf = (char*)malloc(8);
        if (!sbuf) return 0;
        snprintf(sbuf, 8, "%s", s);
        return (uint64_t)sbuf;
    }

    bf_t bx;
    f128_to_bf(&bx, x);
    size_t len;
    char *str = bf_ftoa(&len, &bx, 10, 34, BF_FTOA_FORMAT_FREE_MIN | BF_RNDN);
    bf_delete(&bx);
    if (!str) return 0;
    // bf_ftoa allocates with bf_realloc; copy to malloc'd buffer for consistency
    char *buf = (char*)malloc(len + 1);
    if (!buf) { bf_realloc(&bf_ctx, str, 0); return 0; }
    memcpy(buf, str, len + 1);
    bf_realloc(&bf_ctx, str, 0);
    return (uint64_t)buf;
}

// ============================================================================
// RazorForge-callable ABI bridges
//
// RazorForge codegen represents F128 as the LLVM scalar `fp128`, whose call
// ABI does not match the C ABI of the 16-byte `f128_t` struct on any x86-64
// platform (SysV passes fp128 in SSE registers but f128_t in integer
// registers; Win64 returns fp128 in xmm0 but f128_t via hidden sret).
// These bridges only use ABI-stable parameter forms: f128 inputs arrive as
// (low, high) u64 pairs and f128 results are written through an out pointer
// (same approach as rf_parse_F128 above). The by-value rf_f128_* functions
// remain for C-internal callers (decimal_functions.c, the Win64 builtins).
// ============================================================================

static f128_t f128_from_parts(uint64_t low, uint64_t high)
{
    f128_t v;
    v.low = low;
    v.high = high;
    return v;
}

void rf_f128_sin_parts(uint64_t low, uint64_t high, f128_t* out)    { *out = rf_f128_sin(f128_from_parts(low, high)); }
void rf_f128_cos_parts(uint64_t low, uint64_t high, f128_t* out)    { *out = rf_f128_cos(f128_from_parts(low, high)); }
void rf_f128_tan_parts(uint64_t low, uint64_t high, f128_t* out)    { *out = rf_f128_tan(f128_from_parts(low, high)); }
void rf_f128_asin_parts(uint64_t low, uint64_t high, f128_t* out)   { *out = rf_f128_asin(f128_from_parts(low, high)); }
void rf_f128_acos_parts(uint64_t low, uint64_t high, f128_t* out)   { *out = rf_f128_acos(f128_from_parts(low, high)); }
void rf_f128_atan_parts(uint64_t low, uint64_t high, f128_t* out)   { *out = rf_f128_atan(f128_from_parts(low, high)); }
void rf_f128_sinh_parts(uint64_t low, uint64_t high, f128_t* out)   { *out = rf_f128_sinh(f128_from_parts(low, high)); }
void rf_f128_cosh_parts(uint64_t low, uint64_t high, f128_t* out)   { *out = rf_f128_cosh(f128_from_parts(low, high)); }
void rf_f128_tanh_parts(uint64_t low, uint64_t high, f128_t* out)   { *out = rf_f128_tanh(f128_from_parts(low, high)); }
void rf_f128_asinh_parts(uint64_t low, uint64_t high, f128_t* out)  { *out = rf_f128_asinh(f128_from_parts(low, high)); }
void rf_f128_acosh_parts(uint64_t low, uint64_t high, f128_t* out)  { *out = rf_f128_acosh(f128_from_parts(low, high)); }
void rf_f128_atanh_parts(uint64_t low, uint64_t high, f128_t* out)  { *out = rf_f128_atanh(f128_from_parts(low, high)); }
void rf_f128_exp_parts(uint64_t low, uint64_t high, f128_t* out)    { *out = rf_f128_exp(f128_from_parts(low, high)); }
void rf_f128_exp2_parts(uint64_t low, uint64_t high, f128_t* out)   { *out = rf_f128_exp2(f128_from_parts(low, high)); }
void rf_f128_expm1_parts(uint64_t low, uint64_t high, f128_t* out)  { *out = rf_f128_expm1(f128_from_parts(low, high)); }
void rf_f128_log_parts(uint64_t low, uint64_t high, f128_t* out)    { *out = rf_f128_log(f128_from_parts(low, high)); }
void rf_f128_log2_parts(uint64_t low, uint64_t high, f128_t* out)   { *out = rf_f128_log2(f128_from_parts(low, high)); }
void rf_f128_log10_parts(uint64_t low, uint64_t high, f128_t* out)  { *out = rf_f128_log10(f128_from_parts(low, high)); }
void rf_f128_log1p_parts(uint64_t low, uint64_t high, f128_t* out)  { *out = rf_f128_log1p(f128_from_parts(low, high)); }
void rf_f128_cbrt_parts(uint64_t low, uint64_t high, f128_t* out)   { *out = rf_f128_cbrt(f128_from_parts(low, high)); }

void rf_f128_atan2_parts(uint64_t y_low, uint64_t y_high, uint64_t x_low, uint64_t x_high, f128_t* out)
{
    *out = rf_f128_atan2(f128_from_parts(y_low, y_high), f128_from_parts(x_low, x_high));
}

void rf_f128_pow_parts(uint64_t base_low, uint64_t base_high, uint64_t exp_low, uint64_t exp_high, f128_t* out)
{
    *out = rf_f128_pow(f128_from_parts(base_low, base_high), f128_from_parts(exp_low, exp_high));
}

void rf_f128_hypot_parts(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high, f128_t* out)
{
    *out = rf_f128_hypot(f128_from_parts(x_low, x_high), f128_from_parts(y_low, y_high));
}

void rf_f128_copysign_parts(uint64_t value_low, uint64_t value_high, uint64_t sign_low, uint64_t sign_high, f128_t* out)
{
    // IEEE binary128 sign bit lives in bit 63 of the high word.
    (void)sign_low;
    out->low = value_low;
    out->high = (value_high & 0x7FFFFFFFFFFFFFFFULL) | (sign_high & 0x8000000000000000ULL);
}

uint64_t rf_format_F128_parts(uint64_t low, uint64_t high)
{
    return rf_format_F128(f128_from_parts(low, high));
}

// ---- Arithmetic / sqrt / rounding bridges ----
// Added for the fp128-free codegen: F128 is an i128 bit carrier in LLVM and
// every operation lowers to one of these calls (no fadd/fcmp/fpext on fp128,
// hence no compiler-rt __*tf* builtins anywhere in emitted code).

void rf_f128_add_parts(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high, f128_t* out)
{
    *out = rf_f128_add(f128_from_parts(a_low, a_high), f128_from_parts(b_low, b_high));
}

void rf_f128_sub_parts(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high, f128_t* out)
{
    *out = rf_f128_sub(f128_from_parts(a_low, a_high), f128_from_parts(b_low, b_high));
}

void rf_f128_mul_parts(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high, f128_t* out)
{
    *out = rf_f128_mul(f128_from_parts(a_low, a_high), f128_from_parts(b_low, b_high));
}

void rf_f128_div_parts(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high, f128_t* out)
{
    *out = rf_f128_div(f128_from_parts(a_low, a_high), f128_from_parts(b_low, b_high));
}

void rf_f128_sqrt_parts(uint64_t low, uint64_t high, f128_t* out)  { *out = rf_f128_sqrt(f128_from_parts(low, high)); }
void rf_f128_floor_parts(uint64_t low, uint64_t high, f128_t* out) { *out = rf_f128_floor(f128_from_parts(low, high)); }
void rf_f128_ceil_parts(uint64_t low, uint64_t high, f128_t* out)  { *out = rf_f128_ceil(f128_from_parts(low, high)); }
void rf_f128_round_parts(uint64_t low, uint64_t high, f128_t* out) { *out = rf_f128_round(f128_from_parts(low, high)); }
void rf_f128_trunc_parts(uint64_t low, uint64_t high, f128_t* out) { *out = rf_f128_trunc(f128_from_parts(low, high)); }

// ---- Comparison bridges (scalar int return: ABI-safe by value) ----

int32_t rf_f128_eq_parts(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return rf_f128_eq(f128_from_parts(a_low, a_high), f128_from_parts(b_low, b_high));
}

int32_t rf_f128_lt_parts(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return rf_f128_lt(f128_from_parts(a_low, a_high), f128_from_parts(b_low, b_high));
}

int32_t rf_f128_gt_parts(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return rf_f128_gt(f128_from_parts(a_low, a_high), f128_from_parts(b_low, b_high));
}

// ---- Conversion bridges to F128 ----
// Narrow integer types widen to 64-bit on the RazorForge side first.

void rf_s64_to_f128_parts(int64_t x, f128_t* out)   { *out = rf_s64_to_f128(x); }
void rf_u64_to_f128_parts(uint64_t x, f128_t* out)  { *out = rf_u64_to_f128(x); }

void rf_s128_to_f128_parts(uint64_t low, uint64_t high, f128_t* out)
{
    s128_t v;
    v.low = low;
    v.high = (int64_t)high;
    *out = rf_s128_to_f128(v);
}

void rf_u128_to_f128_parts(uint64_t low, uint64_t high, f128_t* out)
{
    u128_t v;
    v.low = low;
    v.high = high;
    *out = rf_u128_to_f128(v);
}

void rf_f32_to_f128_parts(float x, f128_t* out)  { *out = rf_f32_to_f128(x); }
void rf_f64_to_f128_parts(double x, f128_t* out) { *out = rf_f64_to_f128(x); }

// ---- Conversion bridges from F128 (scalar returns: ABI-safe by value) ----

float rf_f128_to_f32_parts(uint64_t low, uint64_t high)  { return rf_f128_to_f32(f128_from_parts(low, high)); }
double rf_f128_to_f64_parts(uint64_t low, uint64_t high) { return rf_f128_to_f64(f128_from_parts(low, high)); }

#else
#error "LibBF is required for f128 support. Define HAVE_LIBBF and link against LibBF."
#endif // HAVE_LIBBF
