/*
 * RazorForge Runtime - f128 (Quad Precision) Floating Point Functions
 * IEEE 754 binary128 operations using LibBF library
 *
 * LibBF provides arbitrary precision with full transcendental support.
 * We configure it for 113-bit mantissa (IEEE binary128 precision).
 */

#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include "../include/razorforge_math.h"

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

// ============================================================================
// Conversion from other float types
// ============================================================================

f128_t rf_f32_to_f128(float x)
{
    return rf_f64_to_f128((double)x);
}

f128_t rf_f64_to_f128(double x)
{
    f128_t result = {0, 0};

    if (x != x) {  // NaN
        result.high = 0x7FFF800000000000ULL;
        return result;
    }

    if (x == 0.0) {
        if (1.0 / x < 0) result.high = 0x8000000000000000ULL;  // -0
        return result;
    }

    if (x == INFINITY) {
        result.high = 0x7FFF000000000000ULL;
        return result;
    }

    if (x == -INFINITY) {
        result.high = 0xFFFF000000000000ULL;
        return result;
    }

    // Extract f64 components
    union { double d; uint64_t u; } conv;
    conv.d = x;

    int sign = (conv.u >> 63) & 1;
    int exp64 = (conv.u >> 52) & 0x7FF;
    uint64_t mant64 = conv.u & 0x000FFFFFFFFFFFFFULL;

    if (exp64 == 0) {
        // Subnormal f64 -> subnormal or zero f128
        // Approximate as zero for now
        if (sign) result.high = 0x8000000000000000ULL;
        return result;
    }

    // Convert exponent: f64 bias is 1023, f128 bias is 16383
    int exp128 = exp64 - 1023 + F128_EXP_BIAS;

    // Mantissa: f64 has 52 bits, f128 has 112 bits
    // Shift left by (112 - 52) = 60 bits
    uint64_t mant_high = mant64 >> 4;   // Top 48 bits of shifted mantissa
    uint64_t mant_low = mant64 << 60;   // Bottom 64 bits

    result.high = ((uint64_t)sign << 63) | ((uint64_t)exp128 << 48) | mant_high;
    result.low = mant_low;

    return result;
}

float rf_f128_to_f32(f128_t x)
{
    return (float)rf_f128_to_f64(x);
}

double rf_f128_to_f64(f128_t x)
{
    int sign = (x.high >> 63) & 1;
    int exp128 = (x.high >> 48) & 0x7FFF;
    uint64_t mant_high = x.high & 0x0000FFFFFFFFFFFFULL;
    uint64_t mant_low = x.low;

    if (exp128 == 0x7FFF) {
        if (mant_high == 0 && mant_low == 0) {
            return sign ? -INFINITY : INFINITY;
        }
        return NAN;
    }

    if (exp128 == 0 && mant_high == 0 && mant_low == 0) {
        return sign ? -0.0 : 0.0;
    }

    // Convert exponent
    int exp64 = exp128 - F128_EXP_BIAS + 1023;

    if (exp64 >= 0x7FF) {
        return sign ? -INFINITY : INFINITY;  // Overflow
    }
    if (exp64 <= 0) {
        return sign ? -0.0 : 0.0;  // Underflow
    }

    // Convert mantissa: take top 52 bits of 112-bit mantissa
    uint64_t mant64 = (mant_high << 4) | (mant_low >> 60);

    union { uint64_t u; double d; } conv;
    conv.u = ((uint64_t)sign << 63) | ((uint64_t)exp64 << 52) | mant64;

    return conv.d;
}

// ============================================================================
// Conversion from/to integers
// ============================================================================

// Helper: count leading zeros in 64-bit value
static int clz64(uint64_t x)
{
    if (x == 0) return 64;
#if defined(__GNUC__) || defined(__clang__)
    return __builtin_clzll(x);
#else
    int n = 0;
    if ((x & 0xFFFFFFFF00000000ULL) == 0) { n += 32; x <<= 32; }
    if ((x & 0xFFFF000000000000ULL) == 0) { n += 16; x <<= 16; }
    if ((x & 0xFF00000000000000ULL) == 0) { n += 8;  x <<= 8; }
    if ((x & 0xF000000000000000ULL) == 0) { n += 4;  x <<= 4; }
    if ((x & 0xC000000000000000ULL) == 0) { n += 2;  x <<= 2; }
    if ((x & 0x8000000000000000ULL) == 0) { n += 1; }
    return n;
#endif
}

f128_t rf_s32_to_f128(int32_t x)
{
    return rf_s64_to_f128((int64_t)x);
}

f128_t rf_s64_to_f128(int64_t x)
{
    if (x == 0) return rf_f128_zero(0);

    int sign = 0;
    uint64_t abs_x;
    if (x < 0) {
        sign = 1;
        abs_x = (uint64_t)(-(x + 1)) + 1;  // Handle INT64_MIN correctly
    } else {
        abs_x = (uint64_t)x;
    }

    f128_t result = rf_u64_to_f128(abs_x);
    if (sign) result.high |= 0x8000000000000000ULL;
    return result;
}

f128_t rf_u32_to_f128(uint32_t x)
{
    return rf_u64_to_f128((uint64_t)x);
}

f128_t rf_u64_to_f128(uint64_t x)
{
    if (x == 0) return rf_f128_zero(0);

    // Find position of MSB (0-63, where 63 is the highest)
    int msb_pos = 63 - clz64(x);

    // IEEE exponent: value = 1.fraction * 2^(exp - bias)
    // For integer x with MSB at position msb_pos: x = 2^msb_pos * (1 + fraction)
    // So exp - bias = msb_pos, thus exp = msb_pos + bias
    int exp = msb_pos + F128_EXP_BIAS;

    // We need to place the mantissa bits (excluding the implicit 1) into 112 bits
    // The MSB is the implicit 1, so we have msb_pos bits of actual mantissa
    // Shift left to align with the 112-bit mantissa field
    uint64_t mant_high, mant_low;

    if (msb_pos <= 48) {
        // All mantissa bits fit in mant_high (after removing implicit 1)
        // Clear the MSB (implicit 1) and shift left to fill 48 bits
        uint64_t mant = x & ((1ULL << msb_pos) - 1);  // Remove implicit 1
        mant_high = mant << (48 - msb_pos);
        mant_low = 0;
    } else if (msb_pos <= 112) {
        // Mantissa spans both mant_high and mant_low
        uint64_t mant = x & ((1ULL << msb_pos) - 1);  // Remove implicit 1
        int shift = msb_pos - 48;
        mant_high = mant >> shift;
        mant_low = mant << (64 - shift);
    } else {
        // msb_pos > 112 is impossible for 64-bit integers (max msb_pos = 63)
        mant_high = 0;
        mant_low = 0;
    }

    f128_t result;
    result.high = ((uint64_t)exp << 48) | mant_high;
    result.low = mant_low;
    return result;
}

// 128-bit integer to f128 conversions

f128_t rf_u128_to_f128(u128_t x)
{
    // Handle zero
    if (x.high == 0 && x.low == 0) {
        return rf_f128_zero(0);
    }

    // If high is zero, delegate to 64-bit version
    if (x.high == 0) {
        return rf_u64_to_f128(x.low);
    }

    // Find MSB position (0-127, where 127 is the highest possible)
    // MSB is in the high word
    int msb_in_high = 63 - clz64(x.high);  // 0-63 within high word
    int msb_pos = 64 + msb_in_high;        // 64-127 overall

    // IEEE exponent: exp = msb_pos + bias
    int exp = msb_pos + F128_EXP_BIAS;

    // We have 112 explicit mantissa bits in f128
    // msb_pos bits of actual mantissa (excluding implicit 1)
    // Need to extract the top 112 bits of the mantissa

    uint64_t mant_high, mant_low;

    // Clear the MSB (implicit 1) from high word
    uint64_t high_mant = x.high & ((1ULL << msb_in_high) - 1);

    // Mantissa is: high_mant (msb_in_high bits) : x.low (64 bits)
    // Total mantissa bits = msb_in_high + 64 = msb_pos bits
    // We need to fit this into 112 bits: mant_high (48) + mant_low (64)

    if (msb_pos <= 48) {
        // This case won't happen since msb_pos >= 64 when high != 0
        mant_high = 0;
        mant_low = 0;
    } else if (msb_pos <= 112) {
        // msb_pos is 64-112, mantissa fits exactly or with room to spare
        // Shift left to align with 112-bit field
        int shift_left = 112 - msb_pos;  // 0-48
        if (shift_left >= 64) {
            // All of low word goes to mant_high
            mant_high = (high_mant << shift_left) | (x.low << (shift_left - 64));
            mant_low = 0;
        } else if (shift_left > 0) {
            mant_high = (high_mant << shift_left) | (x.low >> (64 - shift_left));
            mant_low = x.low << shift_left;
        } else {
            // shift_left == 0, msb_pos == 112
            mant_high = high_mant;
            mant_low = x.low;
        }
    } else {
        // msb_pos > 112, need to shift right (lose precision)
        int shift_right = msb_pos - 112;  // 1-15 (since max msb_pos = 127)

        // Combine high_mant and x.low, then shift right
        // Result needs to fit in 112 bits: mant_high (48) + mant_low (64)
        if (shift_right < 64) {
            mant_low = (x.low >> shift_right) | (high_mant << (64 - shift_right));
            mant_high = high_mant >> shift_right;
        } else {
            mant_low = high_mant >> (shift_right - 64);
            mant_high = 0;
        }
    }

    // Mask to ensure mant_high is only 48 bits
    mant_high &= 0xFFFFFFFFFFFFULL;

    f128_t result;
    result.high = ((uint64_t)exp << 48) | mant_high;
    result.low = mant_low;
    return result;
}

f128_t rf_s128_to_f128(s128_t x)
{
    // Handle zero
    if (x.high == 0 && x.low == 0) {
        return rf_f128_zero(0);
    }

    int sign = (x.high < 0) ? 1 : 0;
    u128_t abs_x;

    if (sign) {
        // Negate: ~x + 1
        // Handle INT128_MIN (0x8000...0000) correctly
        if (x.high == (int64_t)0x8000000000000000ULL && x.low == 0) {
            // This is INT128_MIN, abs value is 2^127
            abs_x.low = 0;
            abs_x.high = 0x8000000000000000ULL;
        } else {
            // Standard negation
            abs_x.low = ~x.low + 1;
            abs_x.high = ~(uint64_t)x.high + (abs_x.low == 0 ? 1 : 0);
        }
    } else {
        abs_x.low = x.low;
        abs_x.high = (uint64_t)x.high;
    }

    f128_t result = rf_u128_to_f128(abs_x);
    if (sign) {
        result.high |= 0x8000000000000000ULL;
    }
    return result;
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
// Basic arithmetic using LibBF
// ============================================================================

f128_t rf_f128_add(f128_t a, f128_t b)
{
    bf_t ba, bb, br;
    f128_to_bf(&ba, a);
    f128_to_bf(&bb, b);
    bf_init(&bf_ctx, &br);

    bf_add(&br, &ba, &bb, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&ba);
    bf_delete(&bb);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_sub(f128_t a, f128_t b)
{
    bf_t ba, bb, br;
    f128_to_bf(&ba, a);
    f128_to_bf(&bb, b);
    bf_init(&bf_ctx, &br);

    bf_sub(&br, &ba, &bb, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&ba);
    bf_delete(&bb);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_mul(f128_t a, f128_t b)
{
    bf_t ba, bb, br;
    f128_to_bf(&ba, a);
    f128_to_bf(&bb, b);
    bf_init(&bf_ctx, &br);

    bf_mul(&br, &ba, &bb, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&ba);
    bf_delete(&bb);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_div(f128_t a, f128_t b)
{
    bf_t ba, bb, br;
    f128_to_bf(&ba, a);
    f128_to_bf(&bb, b);
    bf_init(&bf_ctx, &br);

    bf_div(&br, &ba, &bb, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&ba);
    bf_delete(&bb);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_sqrt(f128_t a)
{
    bf_t ba, br;
    f128_to_bf(&ba, a);
    bf_init(&bf_ctx, &br);

    bf_sqrt(&br, &ba, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&ba);
    bf_delete(&br);
    return result;
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
    bf_t ba, bb;
    f128_to_bf(&ba, a);
    f128_to_bf(&bb, b);
    int result = bf_cmp_eq(&ba, &bb);
    bf_delete(&ba);
    bf_delete(&bb);
    return result;
}

int rf_f128_lt(f128_t a, f128_t b)
{
    bf_t ba, bb;
    f128_to_bf(&ba, a);
    f128_to_bf(&bb, b);
    int result = bf_cmp_lt(&ba, &bb);
    bf_delete(&ba);
    bf_delete(&bb);
    return result;
}

int rf_f128_le(f128_t a, f128_t b)
{
    bf_t ba, bb;
    f128_to_bf(&ba, a);
    f128_to_bf(&bb, b);
    int result = bf_cmp_le(&ba, &bb);
    bf_delete(&ba);
    bf_delete(&bb);
    return result;
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
// Transcendental functions - FULL PRECISION via LibBF
// ============================================================================

f128_t rf_f128_sin(f128_t x)
{
    bf_t bx, br;
    f128_to_bf(&bx, x);
    bf_init(&bf_ctx, &br);

    bf_sin(&br, &bx, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&bx);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_cos(f128_t x)
{
    bf_t bx, br;
    f128_to_bf(&bx, x);
    bf_init(&bf_ctx, &br);

    bf_cos(&br, &bx, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&bx);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_tan(f128_t x)
{
    bf_t bx, br;
    f128_to_bf(&bx, x);
    bf_init(&bf_ctx, &br);

    bf_tan(&br, &bx, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&bx);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_asin(f128_t x)
{
    bf_t bx, br;
    f128_to_bf(&bx, x);
    bf_init(&bf_ctx, &br);

    bf_asin(&br, &bx, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&bx);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_acos(f128_t x)
{
    bf_t bx, br;
    f128_to_bf(&bx, x);
    bf_init(&bf_ctx, &br);

    bf_acos(&br, &bx, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&bx);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_atan(f128_t x)
{
    bf_t bx, br;
    f128_to_bf(&bx, x);
    bf_init(&bf_ctx, &br);

    bf_atan(&br, &bx, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&bx);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_atan2(f128_t y, f128_t x)
{
    bf_t by, bx, br;
    f128_to_bf(&by, y);
    f128_to_bf(&bx, x);
    bf_init(&bf_ctx, &br);

    bf_atan2(&br, &by, &bx, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&by);
    bf_delete(&bx);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_exp(f128_t x)
{
    bf_t bx, br;
    f128_to_bf(&bx, x);
    bf_init(&bf_ctx, &br);

    bf_exp(&br, &bx, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&bx);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_log(f128_t x)
{
    bf_t bx, br;
    f128_to_bf(&bx, x);
    bf_init(&bf_ctx, &br);

    bf_log(&br, &bx, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&bx);
    bf_delete(&br);
    return result;
}

f128_t rf_f128_pow(f128_t x, f128_t y)
{
    bf_t bx, by, br;
    f128_to_bf(&bx, x);
    f128_to_bf(&by, y);
    bf_init(&bf_ctx, &br);

    bf_pow(&br, &bx, &by, F128_PREC, BF_RNDN);

    f128_t result = bf_to_f128(&br);
    bf_delete(&bx);
    bf_delete(&by);
    bf_delete(&br);
    return result;
}

// ============================================================================
// Derived transcendental functions
// ============================================================================

f128_t rf_f128_sinh(f128_t x)
{
    // sinh(x) = (exp(x) - exp(-x)) / 2
    f128_t ex = rf_f128_exp(x);
    f128_t emx = rf_f128_exp(rf_f128_neg(x));
    f128_t two = rf_f64_to_f128(2.0);
    return rf_f128_div(rf_f128_sub(ex, emx), two);
}

f128_t rf_f128_cosh(f128_t x)
{
    // cosh(x) = (exp(x) + exp(-x)) / 2
    f128_t ex = rf_f128_exp(x);
    f128_t emx = rf_f128_exp(rf_f128_neg(x));
    f128_t two = rf_f64_to_f128(2.0);
    return rf_f128_div(rf_f128_add(ex, emx), two);
}

f128_t rf_f128_tanh(f128_t x)
{
    // tanh(x) = sinh(x) / cosh(x)
    return rf_f128_div(rf_f128_sinh(x), rf_f128_cosh(x));
}

f128_t rf_f128_asinh(f128_t x)
{
    // asinh(x) = log(x + sqrt(x^2 + 1))
    f128_t one = rf_f64_to_f128(1.0);
    f128_t x2 = rf_f128_mul(x, x);
    f128_t inner = rf_f128_sqrt(rf_f128_add(x2, one));
    return rf_f128_log(rf_f128_add(x, inner));
}

f128_t rf_f128_acosh(f128_t x)
{
    // acosh(x) = log(x + sqrt(x^2 - 1))
    f128_t one = rf_f64_to_f128(1.0);
    f128_t x2 = rf_f128_mul(x, x);
    f128_t inner = rf_f128_sqrt(rf_f128_sub(x2, one));
    return rf_f128_log(rf_f128_add(x, inner));
}

f128_t rf_f128_atanh(f128_t x)
{
    // atanh(x) = 0.5 * log((1+x)/(1-x))
    f128_t one = rf_f64_to_f128(1.0);
    f128_t half = rf_f64_to_f128(0.5);
    f128_t num = rf_f128_add(one, x);
    f128_t den = rf_f128_sub(one, x);
    return rf_f128_mul(half, rf_f128_log(rf_f128_div(num, den)));
}

f128_t rf_f128_log2(f128_t x)
{
    // log2(x) = log(x) / log(2)
    f128_t ln2 = rf_f128_log(rf_f64_to_f128(2.0));
    return rf_f128_div(rf_f128_log(x), ln2);
}

f128_t rf_f128_log10(f128_t x)
{
    // log10(x) = log(x) / log(10)
    f128_t ln10 = rf_f128_log(rf_f64_to_f128(10.0));
    return rf_f128_div(rf_f128_log(x), ln10);
}

f128_t rf_f128_exp2(f128_t x)
{
    // exp2(x) = 2^x = exp(x * log(2))
    f128_t ln2 = rf_f128_log(rf_f64_to_f128(2.0));
    return rf_f128_exp(rf_f128_mul(x, ln2));
}

f128_t rf_f128_expm1(f128_t x)
{
    // expm1(x) = exp(x) - 1
    // TODO: Use Taylor series for small x to avoid precision loss
    return rf_f128_sub(rf_f128_exp(x), rf_f64_to_f128(1.0));
}

f128_t rf_f128_log1p(f128_t x)
{
    // log1p(x) = log(1 + x)
    // TODO: Use Taylor series for small x to avoid precision loss
    return rf_f128_log(rf_f128_add(rf_f64_to_f128(1.0), x));
}

f128_t rf_f128_hypot(f128_t x, f128_t y)
{
    // hypot(x, y) = sqrt(x^2 + y^2)
    f128_t x2 = rf_f128_mul(x, x);
    f128_t y2 = rf_f128_mul(y, y);
    return rf_f128_sqrt(rf_f128_add(x2, y2));
}

f128_t rf_f128_cbrt(f128_t x)
{
    // cbrt(x) = x^(1/3)
    f128_t third = rf_f64_to_f128(1.0 / 3.0);
    int neg = rf_f128_is_negative(x);
    if (neg) x = rf_f128_abs(x);
    f128_t result = rf_f128_pow(x, third);
    if (neg) result = rf_f128_neg(result);
    return result;
}

f128_t rf_f128_fmod(f128_t x, f128_t y)
{
    bf_t bx, by, br;
    f128_to_bf(&bx, x);
    f128_to_bf(&by, y);
    bf_init(&bf_ctx, &br);

    bf_rem(&br, &bx, &by, F128_PREC, BF_RNDN, BF_RNDZ);

    f128_t result = bf_to_f128(&br);
    bf_delete(&bx);
    bf_delete(&by);
    bf_delete(&br);
    return result;
}

// ============================================================================
// Rounding
// ============================================================================

f128_t rf_f128_floor(f128_t x)
{
    bf_t bx;
    f128_to_bf(&bx, x);
    bf_rint(&bx, BF_RNDD);
    f128_t result = bf_to_f128(&bx);
    bf_delete(&bx);
    return result;
}

f128_t rf_f128_ceil(f128_t x)
{
    bf_t bx;
    f128_to_bf(&bx, x);
    bf_rint(&bx, BF_RNDU);
    f128_t result = bf_to_f128(&bx);
    bf_delete(&bx);
    return result;
}

f128_t rf_f128_trunc(f128_t x)
{
    bf_t bx;
    f128_to_bf(&bx, x);
    bf_rint(&bx, BF_RNDZ);
    f128_t result = bf_to_f128(&bx);
    bf_delete(&bx);
    return result;
}

f128_t rf_f128_round(f128_t x)
{
    bf_t bx;
    f128_to_bf(&bx, x);
    bf_rint(&bx, BF_RNDNA);  // Round to nearest, ties away from zero
    f128_t result = bf_to_f128(&bx);
    bf_delete(&bx);
    return result;
}

#else
#error "LibBF is required for f128 support. Define HAVE_LIBBF and link against LibBF."
#endif // HAVE_LIBBF
