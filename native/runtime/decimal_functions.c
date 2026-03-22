/*
 * RazorForge Runtime - Decimal Floating Point Functions
 * IEEE 754-2008 decimal floating point operations using Intel Decimal Library
 *
 * This implementation uses Intel's BID (Binary Integer Decimal) library for
 * proper IEEE 754-2008 compliant decimal floating point arithmetic.
 *
 * NOTE: All d128 public functions take split (uint64_t low, uint64_t high)
 * parameters instead of d128_t structs. This is required because LLVM passes
 * {i64, i64} as two register values on Windows x64, but MSVC ABI passes
 * 16-byte structs by pointer. The split form ensures ABI compatibility.
 */

#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include "../include/razorforge_math.h"

// Intel Decimal Library configuration
// Using value semantics (CALL_BY_REF=0) and global rounding (GLOBAL_RND=1)
#include "../inteldecimal/LIBRARY/src/bid_conf.h"
#include "../inteldecimal/LIBRARY/src/bid_functions.h"

// ============================================================================
// d32 operations (decimal32 - 7 significant digits)
// ============================================================================

uint32_t rf_d32_add(uint32_t a, uint32_t b)
{
    return bid32_add(a, b);
}

uint32_t rf_d32_sub(uint32_t a, uint32_t b)
{
    return bid32_sub(a, b);
}

uint32_t rf_d32_mul(uint32_t a, uint32_t b)
{
    return bid32_mul(a, b);
}

uint32_t rf_d32_div(uint32_t a, uint32_t b)
{
    return bid32_div(a, b);
}

int32_t rf_d32_cmp(uint32_t a, uint32_t b)
{
    if (bid32_quiet_less(a, b)) return -1;
    if (bid32_quiet_greater(a, b)) return 1;
    return 0;
}

uint32_t rf_d32_neg(uint32_t a)
{
    return bid32_negate(a);
}

uint32_t rf_d32_from_string(const char* str)
{
    return bid32_from_string((char*)str);
}

char* rf_d32_to_string(uint32_t val)
{
    char* buf = (char*)malloc(64);
    bid32_to_string(buf, val);
    return buf;
}

uint32_t rf_d32_from_s32(int32_t val)
{
    return bid32_from_int32(val);
}

uint32_t rf_d32_from_s64(int64_t val)
{
    return bid32_from_int64(val);
}

uint32_t rf_d32_from_u32(uint32_t val)
{
    return bid32_from_uint32(val);
}

uint32_t rf_d32_from_u64(uint64_t val)
{
    return bid32_from_uint64(val);
}

int32_t rf_d32_to_s32(uint32_t val)
{
    return bid32_to_int32_int(val);
}

int64_t rf_d32_to_s64(uint32_t val)
{
    return bid32_to_int64_int(val);
}

// ============================================================================
// d64 operations (decimal64 - 16 significant digits)
// ============================================================================

uint64_t rf_d64_add(uint64_t a, uint64_t b)
{
    return bid64_add(a, b);
}

uint64_t rf_d64_sub(uint64_t a, uint64_t b)
{
    return bid64_sub(a, b);
}

uint64_t rf_d64_mul(uint64_t a, uint64_t b)
{
    return bid64_mul(a, b);
}

uint64_t rf_d64_div(uint64_t a, uint64_t b)
{
    return bid64_div(a, b);
}

int32_t rf_d64_cmp(uint64_t a, uint64_t b)
{
    if (bid64_quiet_less(a, b)) return -1;
    if (bid64_quiet_greater(a, b)) return 1;
    return 0;
}

uint64_t rf_d64_neg(uint64_t a)
{
    return bid64_negate(a);
}

uint64_t rf_d64_from_string(const char* str)
{
    return bid64_from_string((char*)str);
}

char* rf_d64_to_string(uint64_t val)
{
    char* buf = (char*)malloc(64);
    bid64_to_string(buf, val);
    return buf;
}

uint64_t rf_d64_from_s32(int32_t val)
{
    return bid64_from_int32(val);
}

uint64_t rf_d64_from_s64(int64_t val)
{
    return bid64_from_int64(val);
}

uint64_t rf_d64_from_u32(uint32_t val)
{
    return bid64_from_uint32(val);
}

uint64_t rf_d64_from_u64(uint64_t val)
{
    return bid64_from_uint64(val);
}

int32_t rf_d64_to_s32(uint64_t val)
{
    return bid64_to_int32_int(val);
}

int64_t rf_d64_to_s64(uint64_t val)
{
    return bid64_to_int64_int(val);
}

// ============================================================================
// d128 operations (decimal128 - 34 significant digits)
//
// All public d128 functions take split (uint64_t low, uint64_t high) params
// to avoid LLVM {i64,i64} vs MSVC struct-by-pointer ABI mismatch.
// ============================================================================

// Internal helpers for BID library conversion
static inline BID_UINT128 to_bid128(uint64_t low, uint64_t high)
{
    BID_UINT128 r;
    r.w[0] = low;
    r.w[1] = high;
    return r;
}

static inline d128_t from_bid128(BID_UINT128 x)
{
    d128_t r;
    r.low = x.w[0];
    r.high = x.w[1];
    return r;
}

// Internal d128 -> f128 conversion (C-to-C only, uses d128_t struct)
static f128_t d128_to_f128_internal(uint64_t low, uint64_t high)
{
    BID_UINT128 bid = to_bid128(low, high);
    BID_UINT128 binary;
    binary = bid128_to_binary128(bid);
    f128_t result;
    result.low = binary.w[0];
    result.high = binary.w[1];
    return result;
}

d128_t rf_d128_add(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return from_bid128(bid128_add(to_bid128(a_low, a_high), to_bid128(b_low, b_high)));
}

d128_t rf_d128_sub(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return from_bid128(bid128_sub(to_bid128(a_low, a_high), to_bid128(b_low, b_high)));
}

d128_t rf_d128_mul(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return from_bid128(bid128_mul(to_bid128(a_low, a_high), to_bid128(b_low, b_high)));
}

d128_t rf_d128_div(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return from_bid128(bid128_div(to_bid128(a_low, a_high), to_bid128(b_low, b_high)));
}

int32_t rf_d128_cmp(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    BID_UINT128 ba = to_bid128(a_low, a_high);
    BID_UINT128 bb = to_bid128(b_low, b_high);
    if (bid128_quiet_less(ba, bb)) return -1;
    if (bid128_quiet_greater(ba, bb)) return 1;
    return 0;
}

d128_t rf_d128_neg(uint64_t a_low, uint64_t a_high)
{
    return from_bid128(bid128_negate(to_bid128(a_low, a_high)));
}

d128_t rf_d128_from_string(const char* str)
{
    return from_bid128(bid128_from_string((char*)str));
}

char* rf_d128_to_string(uint64_t low, uint64_t high)
{
    char* buf = (char*)malloc(128);
    bid128_to_string(buf, to_bid128(low, high));
    return buf;
}

d128_t rf_d128_from_s32(int32_t val)
{
    return from_bid128(bid128_from_int32(val));
}

d128_t rf_d128_from_s64(int64_t val)
{
    return from_bid128(bid128_from_int64(val));
}

d128_t rf_d128_from_u32(uint32_t val)
{
    return from_bid128(bid128_from_uint32(val));
}

d128_t rf_d128_from_u64(uint64_t val)
{
    return from_bid128(bid128_from_uint64(val));
}

int32_t rf_d128_to_s32(uint64_t low, uint64_t high)
{
    return bid128_to_int32_int(to_bid128(low, high));
}

int64_t rf_d128_to_s64(uint64_t low, uint64_t high)
{
    return bid128_to_int64_int(to_bid128(low, high));
}

// ============================================================================
// Binary float to decimal conversions
// ============================================================================

uint32_t rf_f32_to_d32(float x)
{
    return binary32_to_bid32(x);
}

uint64_t rf_f32_to_d64(float x)
{
    return binary32_to_bid64(x);
}

d128_t rf_f32_to_d128(float x)
{
    return from_bid128(binary32_to_bid128(x));
}

uint32_t rf_f64_to_d32(double x)
{
    return binary64_to_bid32(x);
}

uint64_t rf_f64_to_d64(double x)
{
    return binary64_to_bid64(x);
}

d128_t rf_f64_to_d128(double x)
{
    return from_bid128(binary64_to_bid128(x));
}

// ============================================================================
// Decimal to binary float conversions
// ============================================================================

float rf_d32_to_f32(uint32_t x)
{
    return bid32_to_binary32(x);
}

double rf_d32_to_f64(uint32_t x)
{
    return bid32_to_binary64(x);
}

uint64_t rf_d32_to_d64(uint32_t x)
{
    return bid32_to_bid64(x);
}

d128_t rf_d32_to_d128(uint32_t x)
{
    return from_bid128(bid32_to_bid128(x));
}

float rf_d64_to_f32(uint64_t x)
{
    return bid64_to_binary32(x);
}

double rf_d64_to_f64(uint64_t x)
{
    return bid64_to_binary64(x);
}

uint32_t rf_d64_to_d32(uint64_t x)
{
    return bid64_to_bid32(x);
}

d128_t rf_d64_to_d128(uint64_t x)
{
    return from_bid128(bid64_to_bid128(x));
}

float rf_d128_to_f32(uint64_t x_low, uint64_t x_high)
{
    return bid128_to_binary32(to_bid128(x_low, x_high));
}

double rf_d128_to_f64(uint64_t x_low, uint64_t x_high)
{
    return bid128_to_binary64(to_bid128(x_low, x_high));
}

uint32_t rf_d128_to_d32(uint64_t x_low, uint64_t x_high)
{
    return bid128_to_bid32(to_bid128(x_low, x_high));
}

uint64_t rf_d128_to_d64(uint64_t x_low, uint64_t x_high)
{
    return bid128_to_bid64(to_bid128(x_low, x_high));
}

// ============================================================================
// d128 <-> f128 conversion
// Intel DFP's BINARY128 is the same layout as f128_t (two uint64_t)
// ============================================================================

f128_t rf_d128_to_f128(uint64_t x_low, uint64_t x_high)
{
    return d128_to_f128_internal(x_low, x_high);
}

d128_t rf_f128_to_d128(f128_t x)
{
    BID_UINT128 binary;
    binary.w[0] = x.low;
    binary.w[1] = x.high;
    return from_bid128(binary128_to_bid128(binary));
}

// ============================================================================
// d128 transcendental functions - FULL PRECISION via LibBF
//
// Strategy: d128 -> f128 -> LibBF transcendental -> f128 -> d128
// LibBF provides arbitrary precision transcendentals with exact rounding.
// Both d128 and f128 have ~34 significant digits.
// ============================================================================

d128_t rf_d128_sin(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_sin(f));
}

d128_t rf_d128_cos(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_cos(f));
}

d128_t rf_d128_tan(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_tan(f));
}

d128_t rf_d128_asin(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_asin(f));
}

d128_t rf_d128_acos(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_acos(f));
}

d128_t rf_d128_atan(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_atan(f));
}

d128_t rf_d128_atan2(uint64_t y_low, uint64_t y_high, uint64_t x_low, uint64_t x_high)
{
    f128_t fy = d128_to_f128_internal(y_low, y_high);
    f128_t fx = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_atan2(fy, fx));
}

d128_t rf_d128_sinh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_sinh(f));
}

d128_t rf_d128_cosh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_cosh(f));
}

d128_t rf_d128_tanh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_tanh(f));
}

d128_t rf_d128_asinh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_asinh(f));
}

d128_t rf_d128_acosh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_acosh(f));
}

d128_t rf_d128_atanh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_atanh(f));
}

d128_t rf_d128_exp(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_exp(f));
}

d128_t rf_d128_exp2(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_exp2(f));
}

d128_t rf_d128_expm1(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_expm1(f));
}

d128_t rf_d128_log(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_log(f));
}

d128_t rf_d128_log2(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_log2(f));
}

d128_t rf_d128_log10(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_log10(f));
}

d128_t rf_d128_log1p(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_log1p(f));
}

d128_t rf_d128_pow(uint64_t base_low, uint64_t base_high, uint64_t exp_low, uint64_t exp_high)
{
    f128_t fb = d128_to_f128_internal(base_low, base_high);
    f128_t fe = d128_to_f128_internal(exp_low, exp_high);
    return rf_f128_to_d128(rf_f128_pow(fb, fe));
}

d128_t rf_d128_cbrt(uint64_t x_low, uint64_t x_high)
{
    f128_t f = d128_to_f128_internal(x_low, x_high);
    return rf_f128_to_d128(rf_f128_cbrt(f));
}

d128_t rf_d128_hypot(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high)
{
    f128_t fx = d128_to_f128_internal(x_low, x_high);
    f128_t fy = d128_to_f128_internal(y_low, y_high);
    return rf_f128_to_d128(rf_f128_hypot(fx, fy));
}

// ============================================================================
// d32 math functions (basic operations that don't require float128 emulation)
// ============================================================================

uint32_t rf_d32_sqrt(uint32_t x)
{
    return bid32_sqrt(x);
}

uint32_t rf_d32_abs(uint32_t x)
{
    return bid32_abs(x);
}

uint32_t rf_d32_ceil(uint32_t x)
{
    return bid32_round_integral_positive(x);
}

uint32_t rf_d32_floor(uint32_t x)
{
    return bid32_round_integral_negative(x);
}

uint32_t rf_d32_round(uint32_t x)
{
    return bid32_round_integral_nearest_away(x);
}

uint32_t rf_d32_trunc(uint32_t x)
{
    return bid32_round_integral_zero(x);
}

uint32_t rf_d32_fmod(uint32_t x, uint32_t y)
{
    return bid32_fmod(x, y);
}

uint32_t rf_d32_fma(uint32_t x, uint32_t y, uint32_t z)
{
    return bid32_fma(x, y, z);
}

uint32_t rf_d32_min(uint32_t x, uint32_t y)
{
    return bid32_minnum(x, y);
}

uint32_t rf_d32_max(uint32_t x, uint32_t y)
{
    return bid32_maxnum(x, y);
}

int32_t rf_d32_isnan(uint32_t x)
{
    return bid32_isNaN(x);
}

int32_t rf_d32_isinf(uint32_t x)
{
    return bid32_isInf(x);
}

int32_t rf_d32_isfinite(uint32_t x)
{
    return bid32_isFinite(x);
}

int32_t rf_d32_isnormal(uint32_t x)
{
    return bid32_isNormal(x);
}

int32_t rf_d32_iszero(uint32_t x)
{
    return bid32_isZero(x);
}

int32_t rf_d32_signbit(uint32_t x)
{
    return bid32_isSigned(x);
}

// ============================================================================
// d64 math functions (basic operations that don't require float128 emulation)
// ============================================================================

uint64_t rf_d64_sqrt(uint64_t x)
{
    return bid64_sqrt(x);
}

uint64_t rf_d64_abs(uint64_t x)
{
    return bid64_abs(x);
}

uint64_t rf_d64_ceil(uint64_t x)
{
    return bid64_round_integral_positive(x);
}

uint64_t rf_d64_floor(uint64_t x)
{
    return bid64_round_integral_negative(x);
}

uint64_t rf_d64_round(uint64_t x)
{
    return bid64_round_integral_nearest_away(x);
}

uint64_t rf_d64_trunc(uint64_t x)
{
    return bid64_round_integral_zero(x);
}

uint64_t rf_d64_fmod(uint64_t x, uint64_t y)
{
    return bid64_fmod(x, y);
}

uint64_t rf_d64_fma(uint64_t x, uint64_t y, uint64_t z)
{
    return bid64_fma(x, y, z);
}

uint64_t rf_d64_min(uint64_t x, uint64_t y)
{
    return bid64_minnum(x, y);
}

uint64_t rf_d64_max(uint64_t x, uint64_t y)
{
    return bid64_maxnum(x, y);
}

int32_t rf_d64_isnan(uint64_t x)
{
    return bid64_isNaN(x);
}

int32_t rf_d64_isinf(uint64_t x)
{
    return bid64_isInf(x);
}

int32_t rf_d64_isfinite(uint64_t x)
{
    return bid64_isFinite(x);
}

int32_t rf_d64_isnormal(uint64_t x)
{
    return bid64_isNormal(x);
}

int32_t rf_d64_iszero(uint64_t x)
{
    return bid64_isZero(x);
}

int32_t rf_d64_signbit(uint64_t x)
{
    return bid64_isSigned(x);
}

// ============================================================================
// d128 math functions (basic operations that don't require float128 emulation)
// ============================================================================

d128_t rf_d128_sqrt(uint64_t x_low, uint64_t x_high)
{
    return from_bid128(bid128_sqrt(to_bid128(x_low, x_high)));
}

d128_t rf_d128_abs(uint64_t x_low, uint64_t x_high)
{
    return from_bid128(bid128_abs(to_bid128(x_low, x_high)));
}

d128_t rf_d128_ceil(uint64_t x_low, uint64_t x_high)
{
    return from_bid128(bid128_round_integral_positive(to_bid128(x_low, x_high)));
}

d128_t rf_d128_floor(uint64_t x_low, uint64_t x_high)
{
    return from_bid128(bid128_round_integral_negative(to_bid128(x_low, x_high)));
}

d128_t rf_d128_round(uint64_t x_low, uint64_t x_high)
{
    return from_bid128(bid128_round_integral_nearest_away(to_bid128(x_low, x_high)));
}

d128_t rf_d128_trunc(uint64_t x_low, uint64_t x_high)
{
    return from_bid128(bid128_round_integral_zero(to_bid128(x_low, x_high)));
}

d128_t rf_d128_fmod(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high)
{
    return from_bid128(bid128_fmod(to_bid128(x_low, x_high), to_bid128(y_low, y_high)));
}

d128_t rf_d128_fma(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high, uint64_t z_low, uint64_t z_high)
{
    return from_bid128(bid128_fma(to_bid128(x_low, x_high), to_bid128(y_low, y_high), to_bid128(z_low, z_high)));
}

d128_t rf_d128_min(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high)
{
    return from_bid128(bid128_minnum(to_bid128(x_low, x_high), to_bid128(y_low, y_high)));
}

d128_t rf_d128_max(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high)
{
    return from_bid128(bid128_maxnum(to_bid128(x_low, x_high), to_bid128(y_low, y_high)));
}

int32_t rf_d128_isnan(uint64_t x_low, uint64_t x_high)
{
    return bid128_isNaN(to_bid128(x_low, x_high));
}

int32_t rf_d128_isinf(uint64_t x_low, uint64_t x_high)
{
    return bid128_isInf(to_bid128(x_low, x_high));
}

int32_t rf_d128_isfinite(uint64_t x_low, uint64_t x_high)
{
    return bid128_isFinite(to_bid128(x_low, x_high));
}

int32_t rf_d128_isnormal(uint64_t x_low, uint64_t x_high)
{
    return bid128_isNormal(to_bid128(x_low, x_high));
}

int32_t rf_d128_iszero(uint64_t x_low, uint64_t x_high)
{
    return bid128_isZero(to_bid128(x_low, x_high));
}

int32_t rf_d128_signbit(uint64_t x_low, uint64_t x_high)
{
    return bid128_isSigned(to_bid128(x_low, x_high));
}

// ============================================================================
// Special values
// ============================================================================

uint32_t rf_d32_nan(void)
{
    return bid32_nan("");
}

uint32_t rf_d32_inf(void)
{
    return bid32_inf();
}

uint32_t rf_d32_neg_inf(void)
{
    return bid32_negate(bid32_inf());
}

uint64_t rf_d64_nan(void)
{
    return bid64_nan("");
}

uint64_t rf_d64_inf(void)
{
    return bid64_inf();
}

uint64_t rf_d64_neg_inf(void)
{
    return bid64_negate(bid64_inf());
}

d128_t rf_d128_nan(void)
{
    return from_bid128(bid128_nan(""));
}

d128_t rf_d128_inf(void)
{
    return from_bid128(bid128_inf());
}

d128_t rf_d128_neg_inf(void)
{
    return from_bid128(bid128_negate(bid128_inf()));
}

// ============================================================================
// Comparison predicates
// ============================================================================

int32_t rf_d32_eq(uint32_t a, uint32_t b)
{
    return bid32_quiet_equal(a, b);
}

int32_t rf_d32_ne(uint32_t a, uint32_t b)
{
    return bid32_quiet_not_equal(a, b);
}

int32_t rf_d32_lt(uint32_t a, uint32_t b)
{
    return bid32_quiet_less(a, b);
}

int32_t rf_d32_le(uint32_t a, uint32_t b)
{
    return bid32_quiet_less_equal(a, b);
}

int32_t rf_d32_gt(uint32_t a, uint32_t b)
{
    return bid32_quiet_greater(a, b);
}

int32_t rf_d32_ge(uint32_t a, uint32_t b)
{
    return bid32_quiet_greater_equal(a, b);
}

int32_t rf_d64_eq(uint64_t a, uint64_t b)
{
    return bid64_quiet_equal(a, b);
}

int32_t rf_d64_ne(uint64_t a, uint64_t b)
{
    return bid64_quiet_not_equal(a, b);
}

int32_t rf_d64_lt(uint64_t a, uint64_t b)
{
    return bid64_quiet_less(a, b);
}

int32_t rf_d64_le(uint64_t a, uint64_t b)
{
    return bid64_quiet_less_equal(a, b);
}

int32_t rf_d64_gt(uint64_t a, uint64_t b)
{
    return bid64_quiet_greater(a, b);
}

int32_t rf_d64_ge(uint64_t a, uint64_t b)
{
    return bid64_quiet_greater_equal(a, b);
}

int32_t rf_d128_eq(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return bid128_quiet_equal(to_bid128(a_low, a_high), to_bid128(b_low, b_high));
}

int32_t rf_d128_ne(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return bid128_quiet_not_equal(to_bid128(a_low, a_high), to_bid128(b_low, b_high));
}

int32_t rf_d128_lt(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return bid128_quiet_less(to_bid128(a_low, a_high), to_bid128(b_low, b_high));
}

int32_t rf_d128_le(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return bid128_quiet_less_equal(to_bid128(a_low, a_high), to_bid128(b_low, b_high));
}

int32_t rf_d128_gt(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return bid128_quiet_greater(to_bid128(a_low, a_high), to_bid128(b_low, b_high));
}

int32_t rf_d128_ge(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return bid128_quiet_greater_equal(to_bid128(a_low, a_high), to_bid128(b_low, b_high));
}
