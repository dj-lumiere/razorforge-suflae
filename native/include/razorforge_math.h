#ifndef RAZORFORGE_MATH_H
#define RAZORFORGE_MATH_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// ============================================================================
// IEEE 754-2008 Decimal Floating Point Types
// Using Intel Decimal Library (BID format)
// ============================================================================

// d128 type (decimal128 - 34 significant digits)
typedef struct d128_t {
    uint64_t low;
    uint64_t high;
} d128_t;

// Forward declaration of b128_t (defined later in this header)
typedef struct b128_t b128_t;

// ============================================================================
// d32 operations (decimal32 - 7 significant digits)
// ============================================================================

// Arithmetic
uint32_t d32_add(uint32_t a, uint32_t b);
uint32_t d32_sub(uint32_t a, uint32_t b);
uint32_t d32_mul(uint32_t a, uint32_t b);
uint32_t d32_div(uint32_t a, uint32_t b);
uint32_t d32_neg(uint32_t a);
int32_t d32_cmp(uint32_t a, uint32_t b);

// Conversion from/to string
uint32_t d32_from_string(const char* str);
char* d32_to_string(uint32_t val);

// Conversion from integers
uint32_t d32_from_s32(int32_t val);
uint32_t d32_from_s64(int64_t val);
uint32_t d32_from_u32(uint32_t val);
uint32_t d32_from_u64(uint64_t val);

// Conversion to integers
int32_t d32_to_s32(uint32_t val);
int64_t d32_to_s64(uint32_t val);
uint32_t d32_to_u32(uint32_t val);
uint64_t d32_to_u64(uint32_t val);

// Conversion to other decimal types
uint64_t d32_to_d64(uint32_t x);
d128_t d32_to_d128(uint32_t x);

// ============================================================================
// d64 operations (decimal64 - 16 significant digits)
// ============================================================================

// Arithmetic
uint64_t d64_add(uint64_t a, uint64_t b);
uint64_t d64_sub(uint64_t a, uint64_t b);
uint64_t d64_mul(uint64_t a, uint64_t b);
uint64_t d64_div(uint64_t a, uint64_t b);
uint64_t d64_neg(uint64_t a);
int32_t d64_cmp(uint64_t a, uint64_t b);

// Conversion from/to string
uint64_t d64_from_string(const char* str);
char* d64_to_string(uint64_t val);

// Conversion from integers
uint64_t d64_from_s32(int32_t val);
uint64_t d64_from_s64(int64_t val);
uint64_t d64_from_u32(uint32_t val);
uint64_t d64_from_u64(uint64_t val);

// Conversion to integers
int32_t d64_to_s32(uint64_t val);
int64_t d64_to_s64(uint64_t val);

// Conversion to other decimal types
uint32_t d64_to_d32(uint64_t x);
d128_t d64_to_d128(uint64_t x);

// ============================================================================
// d128 operations (decimal128 - 34 significant digits)
// All d128 functions take split (uint64_t low, uint64_t high) params
// to avoid LLVM {i64,i64} vs MSVC struct-by-pointer ABI mismatch.
// ============================================================================

// Arithmetic
d128_t rf_d128_add(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high);
d128_t rf_d128_sub(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high);
d128_t rf_d128_mul(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high);
d128_t rf_d128_div(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high);
d128_t rf_d128_neg(uint64_t a_low, uint64_t a_high);
int32_t rf_d128_cmp(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high);

// Conversion from/to string
d128_t rf_d128_from_string(const char* str);
char* rf_d128_to_string(uint64_t low, uint64_t high);

// Conversion from integers
d128_t rf_d128_from_s32(int32_t val);
d128_t rf_d128_from_s64(int64_t val);
d128_t rf_d128_from_u32(uint32_t val);
d128_t rf_d128_from_u64(uint64_t val);

// Conversion to integers
int32_t rf_d128_to_s32(uint64_t low, uint64_t high);
int64_t rf_d128_to_s64(uint64_t low, uint64_t high);

// Conversion to other decimal types
uint32_t rf_d128_to_d32(uint64_t x_low, uint64_t x_high);
uint64_t rf_d128_to_d64(uint64_t x_low, uint64_t x_high);

// ============================================================================
// Binary float to decimal conversions
// ============================================================================

uint32_t rf_b32_to_d32(float x);
uint64_t rf_b32_to_d64(float x);
d128_t rf_b32_to_d128(float x);

uint32_t rf_b64_to_d32(double x);
uint64_t rf_b64_to_d64(double x);
d128_t rf_b64_to_d128(double x);

// ============================================================================
// Decimal to binary float conversions
// ============================================================================

float rf_d32_to_b32(uint32_t x);
double rf_d32_to_b64(uint32_t x);
uint64_t rf_d32_to_d64(uint32_t x);
d128_t rf_d32_to_d128(uint32_t x);

float rf_d64_to_b32(uint64_t x);
double rf_d64_to_b64(uint64_t x);
uint32_t rf_d64_to_d32(uint64_t x);
d128_t rf_d64_to_d128(uint64_t x);

float rf_d128_to_b32(uint64_t x_low, uint64_t x_high);
double rf_d128_to_b64(uint64_t x_low, uint64_t x_high);
uint32_t rf_d128_to_d32(uint64_t x_low, uint64_t x_high);
uint64_t rf_d128_to_d64(uint64_t x_low, uint64_t x_high);

// ============================================================================
// d32 math functions
// Basic operations that don't require float128 emulation
// NOTE: Transcendental functions (sin, cos, exp, log, etc.) require Intel's
// float128 emulation code which needs their full build system.
// For transcendental decimal math, use rf_bigdec_* functions instead.
// ============================================================================

uint32_t rf_d32_sqrt(uint32_t x);
uint32_t rf_d32_abs(uint32_t x);
uint32_t rf_d32_ceil(uint32_t x);
uint32_t rf_d32_floor(uint32_t x);
uint32_t rf_d32_round(uint32_t x);
uint32_t rf_d32_trunc(uint32_t x);

uint32_t rf_d32_fmod(uint32_t x, uint32_t y);
uint32_t rf_d32_fma(uint32_t x, uint32_t y, uint32_t z);
uint32_t rf_d32_min(uint32_t x, uint32_t y);
uint32_t rf_d32_max(uint32_t x, uint32_t y);

int32_t rf_d32_isnan(uint32_t x);
int32_t rf_d32_isinf(uint32_t x);
int32_t rf_d32_isfinite(uint32_t x);
int32_t rf_d32_isnormal(uint32_t x);
int32_t rf_d32_iszero(uint32_t x);
int32_t rf_d32_signbit(uint32_t x);

// ============================================================================
// d64 math functions
// Basic operations that don't require float128 emulation
// ============================================================================

uint64_t rf_d64_sqrt(uint64_t x);
uint64_t rf_d64_abs(uint64_t x);
uint64_t rf_d64_ceil(uint64_t x);
uint64_t rf_d64_floor(uint64_t x);
uint64_t rf_d64_round(uint64_t x);
uint64_t rf_d64_trunc(uint64_t x);

uint64_t rf_d64_fmod(uint64_t x, uint64_t y);
uint64_t rf_d64_fma(uint64_t x, uint64_t y, uint64_t z);
uint64_t rf_d64_min(uint64_t x, uint64_t y);
uint64_t rf_d64_max(uint64_t x, uint64_t y);

int32_t rf_d64_isnan(uint64_t x);
int32_t rf_d64_isinf(uint64_t x);
int32_t rf_d64_isfinite(uint64_t x);
int32_t rf_d64_isnormal(uint64_t x);
int32_t rf_d64_iszero(uint64_t x);
int32_t rf_d64_signbit(uint64_t x);

// ============================================================================
// d128 math functions
// Basic operations that don't require float128 emulation
// ============================================================================

d128_t rf_d128_sqrt(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_abs(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_ceil(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_floor(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_round(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_trunc(uint64_t x_low, uint64_t x_high);

d128_t rf_d128_fmod(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high);
d128_t rf_d128_fma(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high, uint64_t z_low, uint64_t z_high);
d128_t rf_d128_min(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high);
d128_t rf_d128_max(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high);

int32_t rf_d128_isnan(uint64_t x_low, uint64_t x_high);
int32_t rf_d128_isinf(uint64_t x_low, uint64_t x_high);
int32_t rf_d128_isfinite(uint64_t x_low, uint64_t x_high);
int32_t rf_d128_isnormal(uint64_t x_low, uint64_t x_high);
int32_t rf_d128_iszero(uint64_t x_low, uint64_t x_high);
int32_t rf_d128_signbit(uint64_t x_low, uint64_t x_high);

// ============================================================================
// d128 <-> b128 conversion
// Both types have ~34 decimal digits precision
// ============================================================================

b128_t rf_d128_to_b128(uint64_t x_low, uint64_t x_high);
d128_t rf_b128_to_d128(b128_t x);

// ============================================================================
// Decimal transcendental functions (tiered TLFloat routing)
// decimal -> exact decimal string -> next-size-up TLFloat binary format ->
// correctly-rounded op -> full-precision string -> decimal.
// Tiers: d32 -> binary64, d64 -> quad, d128 -> octuple. Each intermediate's
// slack dwarfs the target's precision, so the final decimal rounding is the
// only rounding that matters; results are byte-identical across platforms.
// ============================================================================

uint32_t rf_d32_sin(uint32_t x);
uint32_t rf_d32_cos(uint32_t x);
uint32_t rf_d32_tan(uint32_t x);
uint32_t rf_d32_asin(uint32_t x);
uint32_t rf_d32_acos(uint32_t x);
uint32_t rf_d32_atan(uint32_t x);
uint32_t rf_d32_atan2(uint32_t y, uint32_t x);
uint32_t rf_d32_sinh(uint32_t x);
uint32_t rf_d32_cosh(uint32_t x);
uint32_t rf_d32_tanh(uint32_t x);
uint32_t rf_d32_asinh(uint32_t x);
uint32_t rf_d32_acosh(uint32_t x);
uint32_t rf_d32_atanh(uint32_t x);
uint32_t rf_d32_exp(uint32_t x);
uint32_t rf_d32_exp2(uint32_t x);
uint32_t rf_d32_expm1(uint32_t x);
uint32_t rf_d32_log(uint32_t x);
uint32_t rf_d32_log2(uint32_t x);
uint32_t rf_d32_log10(uint32_t x);
uint32_t rf_d32_log1p(uint32_t x);
uint32_t rf_d32_pow(uint32_t base, uint32_t exp);
uint32_t rf_d32_cbrt(uint32_t x);
uint32_t rf_d32_hypot(uint32_t x, uint32_t y);

uint64_t rf_d64_sin(uint64_t x);
uint64_t rf_d64_cos(uint64_t x);
uint64_t rf_d64_tan(uint64_t x);
uint64_t rf_d64_asin(uint64_t x);
uint64_t rf_d64_acos(uint64_t x);
uint64_t rf_d64_atan(uint64_t x);
uint64_t rf_d64_atan2(uint64_t y, uint64_t x);
uint64_t rf_d64_sinh(uint64_t x);
uint64_t rf_d64_cosh(uint64_t x);
uint64_t rf_d64_tanh(uint64_t x);
uint64_t rf_d64_asinh(uint64_t x);
uint64_t rf_d64_acosh(uint64_t x);
uint64_t rf_d64_atanh(uint64_t x);
uint64_t rf_d64_exp(uint64_t x);
uint64_t rf_d64_exp2(uint64_t x);
uint64_t rf_d64_expm1(uint64_t x);
uint64_t rf_d64_log(uint64_t x);
uint64_t rf_d64_log2(uint64_t x);
uint64_t rf_d64_log10(uint64_t x);
uint64_t rf_d64_log1p(uint64_t x);
uint64_t rf_d64_pow(uint64_t base, uint64_t exp);
uint64_t rf_d64_cbrt(uint64_t x);
uint64_t rf_d64_hypot(uint64_t x, uint64_t y);

d128_t rf_d128_sin(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_cos(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_tan(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_asin(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_acos(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_atan(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_atan2(uint64_t y_low, uint64_t y_high, uint64_t x_low, uint64_t x_high);
d128_t rf_d128_sinh(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_cosh(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_tanh(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_asinh(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_acosh(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_atanh(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_exp(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_exp2(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_expm1(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_log(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_log2(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_log10(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_log1p(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_pow(uint64_t base_low, uint64_t base_high, uint64_t exp_low, uint64_t exp_high);
d128_t rf_d128_cbrt(uint64_t x_low, uint64_t x_high);
d128_t rf_d128_hypot(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high);

// ============================================================================
// Special values
// ============================================================================

uint32_t rf_d32_nan(void);
uint32_t rf_d32_inf(void);
uint32_t rf_d32_neg_inf(void);

uint64_t rf_d64_nan(void);
uint64_t rf_d64_inf(void);
uint64_t rf_d64_neg_inf(void);

d128_t rf_d128_nan(void);
d128_t rf_d128_inf(void);
d128_t rf_d128_neg_inf(void);

// ============================================================================
// Comparison predicates
// ============================================================================

int32_t rf_d32_eq(uint32_t a, uint32_t b);
int32_t rf_d32_ne(uint32_t a, uint32_t b);
int32_t rf_d32_lt(uint32_t a, uint32_t b);
int32_t rf_d32_le(uint32_t a, uint32_t b);
int32_t rf_d32_gt(uint32_t a, uint32_t b);
int32_t rf_d32_ge(uint32_t a, uint32_t b);

int32_t rf_d64_eq(uint64_t a, uint64_t b);
int32_t rf_d64_ne(uint64_t a, uint64_t b);
int32_t rf_d64_lt(uint64_t a, uint64_t b);
int32_t rf_d64_le(uint64_t a, uint64_t b);
int32_t rf_d64_gt(uint64_t a, uint64_t b);
int32_t rf_d64_ge(uint64_t a, uint64_t b);

int32_t rf_d128_eq(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high);
int32_t rf_d128_ne(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high);
int32_t rf_d128_lt(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high);
int32_t rf_d128_le(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high);
int32_t rf_d128_gt(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high);
int32_t rf_d128_ge(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high);

// ============================================================================
// Arbitrary precision decimal (rf_bigdecimal)
// Backed by decNumber (the same library that provides D32/D64/D128 above).
// ============================================================================

// Opaque handle for an arbitrary-precision decimal value.
typedef void* rf_bigdecimal;

// Precision control (global working precision for ops without an explicit
// per-call precision argument). Clamped to [1, DECNUMDIGITS] and DEC_MAX_MATH.
void rf_bigdec_set_precision(int digits);
int rf_bigdec_get_precision(void);

// Lifecycle management
rf_bigdecimal rf_bigdec_new(void);
void rf_bigdec_free(rf_bigdecimal a);
rf_bigdecimal rf_bigdec_copy(rf_bigdecimal a);

// Initialization
void rf_bigdec_set_s64(rf_bigdecimal a, int64_t val);
void rf_bigdec_set_b64(rf_bigdecimal a, double val);
void rf_bigdec_set_str(rf_bigdecimal a, const char* str);

// Conversion
int64_t rf_bigdec_get_s64(rf_bigdecimal a);
double rf_bigdec_get_b64(rf_bigdecimal a);
char* rf_bigdec_get_str(rf_bigdecimal a, int decimal_places);

// Arithmetic operations (with precision parameter)
void rf_bigdec_add(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b);
void rf_bigdec_sub(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b);
void rf_bigdec_mul(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b);
void rf_bigdec_div(rf_bigdecimal result, int precision, rf_bigdecimal a, rf_bigdecimal b);
void rf_bigdec_neg(rf_bigdecimal result, rf_bigdecimal a);
void rf_bigdec_abs(rf_bigdecimal result, rf_bigdecimal a);

// Comparison
int rf_bigdec_cmp(rf_bigdecimal a, rf_bigdecimal b); // -1, 0, 1
int rf_bigdec_is_zero(rf_bigdecimal a);
int rf_bigdec_is_neg(rf_bigdecimal a);

// Math functions (with precision parameter)
void rf_bigdec_sqrt(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_pow(rf_bigdecimal result, int precision, rf_bigdecimal base, rf_bigdecimal exp);
void rf_bigdec_exp(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_log(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_log10(rf_bigdecimal result, int precision, rf_bigdecimal a);

// Trigonometric / hyperbolic / constants — LibBF-backed (decNumber has no
// trig; MPFR is LGPL). Binary working precision scales with the request
// (digits * log2(10) + guard), so results are correct at any precision.
void rf_bigdec_sin(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_cos(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_tan(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_asin(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_acos(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_atan(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_sinh(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_cosh(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_tanh(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_pi(rf_bigdecimal result, int precision);
void rf_bigdec_e(rf_bigdecimal result, int precision);

// Rounding
void rf_bigdec_ceil(rf_bigdecimal result, rf_bigdecimal a);
void rf_bigdec_floor(rf_bigdecimal result, rf_bigdecimal a);
void rf_bigdec_round(rf_bigdecimal result, int decimal_places, rf_bigdecimal a);
void rf_bigdec_trunc(rf_bigdecimal result, int decimal_places, rf_bigdecimal a);

// ============================================================================
// cmath-compliant math functions for binary floating point types
// ============================================================================

// b16 (half-precision) math + text conversion is now pure RazorForge (see
// Standard/RazorForge/Core/Numerics/B16.rf + FloatConvert.rf): transcendentals
// route through the b32 path, and formatting/parsing is exact-rational. The old
// b16_functions.c runtime and its rf_b16_* ABI have been removed.

// b32 (float) math functions
float rf_b32_sin(float x);
float rf_b32_cos(float x);
float rf_b32_tan(float x);
float rf_b32_asin(float x);
float rf_b32_acos(float x);
float rf_b32_atan(float x);
float rf_b32_atan2(float y, float x);
float rf_b32_sinh(float x);
float rf_b32_cosh(float x);
float rf_b32_tanh(float x);
float rf_b32_asinh(float x);
float rf_b32_acosh(float x);
float rf_b32_atanh(float x);
float rf_b32_exp(float x);
float rf_b32_exp2(float x);
float rf_b32_expm1(float x);
float rf_b32_log(float x);
float rf_b32_log2(float x);
float rf_b32_log10(float x);
float rf_b32_log1p(float x);
float rf_b32_pow(float base, float exp);
float rf_b32_sqrt(float x);
float rf_b32_cbrt(float x);
float rf_b32_hypot(float x, float y);
float rf_b32_ceil(float x);
float rf_b32_floor(float x);
float rf_b32_trunc(float x);
float rf_b32_round(float x);
float rf_b32_fabs(float x);
float rf_b32_fmod(float x, float y);
float rf_b32_remainder(float x, float y);
float rf_b32_fma(float x, float y, float z);
float rf_b32_fmin(float x, float y);
float rf_b32_fmax(float x, float y);
float rf_b32_copysign(float x, float y);
int32_t rf_b32_isnan(float x);
int32_t rf_b32_isinf(float x);
int32_t rf_b32_isfinite(float x);
int32_t rf_b32_isnormal(float x);
int32_t rf_b32_signbit(float x);

// b64 (double) math functions
double rf_b64_sin(double x);
double rf_b64_cos(double x);
double rf_b64_tan(double x);
double rf_b64_asin(double x);
double rf_b64_acos(double x);
double rf_b64_atan(double x);
double rf_b64_atan2(double y, double x);
double rf_b64_sinh(double x);
double rf_b64_cosh(double x);
double rf_b64_tanh(double x);
double rf_b64_asinh(double x);
double rf_b64_acosh(double x);
double rf_b64_atanh(double x);
double rf_b64_exp(double x);
double rf_b64_exp2(double x);
double rf_b64_expm1(double x);
double rf_b64_log(double x);
double rf_b64_log2(double x);
double rf_b64_log10(double x);
double rf_b64_log1p(double x);
double rf_b64_pow(double base, double exp);
double rf_b64_sqrt(double x);
double rf_b64_cbrt(double x);
double rf_b64_hypot(double x, double y);
double rf_b64_ceil(double x);
double rf_b64_floor(double x);
double rf_b64_trunc(double x);
double rf_b64_round(double x);
double rf_b64_fabs(double x);
double rf_b64_fmod(double x, double y);
double rf_b64_remainder(double x, double y);
double rf_b64_fma(double x, double y, double z);
double rf_b64_fmin(double x, double y);
double rf_b64_fmax(double x, double y);
double rf_b64_copysign(double x, double y);
int32_t rf_b64_isnan(double x);
int32_t rf_b64_isinf(double x);
int32_t rf_b64_isfinite(double x);
int32_t rf_b64_isnormal(double x);
int32_t rf_b64_signbit(double x);

// ============================================================================
// b128 (quad-precision float) math functions
// IEEE 754 binary128: 1 sign, 15 exponent, 112 mantissa bits
// Range: ~3.4e-4932 to ~1.2e4932, ~34 decimal digits precision
//
// NOTE: b128 is implemented via Berkeley SoftFloat library.
// All operations are software-emulated for cross-platform compatibility.
// ============================================================================

// b128 type (quad precision - 128 bits)
typedef struct b128_t {
    uint64_t low;
    uint64_t high;
} b128_t;

// Conversion functions
b128_t rf_b128_from_b32(float x);
b128_t rf_b128_from_b64(double x);
float rf_b128_to_b32(b128_t x);
double rf_b128_to_b64(b128_t x);

// Conversion from/to integers
b128_t rf_b128_from_s32(int32_t x);
b128_t rf_b128_from_s64(int64_t x);
b128_t rf_b128_from_u32(uint32_t x);
b128_t rf_b128_from_u64(uint64_t x);
int32_t rf_b128_to_s32(b128_t x);
int64_t rf_b128_to_s64(b128_t x);
uint32_t rf_b128_to_u32(b128_t x);
uint64_t rf_b128_to_u64(b128_t x);

// Conversion from/to string
b128_t rf_b128_from_string(const char* str);
char* rf_b128_to_string(b128_t x);

// Arithmetic
b128_t rf_b128_add(b128_t a, b128_t b);
b128_t rf_b128_sub(b128_t a, b128_t b);
b128_t rf_b128_mul(b128_t a, b128_t b);
b128_t rf_b128_div(b128_t a, b128_t b);
b128_t rf_b128_neg(b128_t x);

// Comparison
int32_t rf_b128_eq(b128_t a, b128_t b);
int32_t rf_b128_ne(b128_t a, b128_t b);
int32_t rf_b128_lt(b128_t a, b128_t b);
int32_t rf_b128_le(b128_t a, b128_t b);
int32_t rf_b128_gt(b128_t a, b128_t b);
int32_t rf_b128_ge(b128_t a, b128_t b);
int32_t rf_b128_cmp(b128_t a, b128_t b);  // Returns -1, 0, 1

// Basic math
b128_t rf_b128_abs(b128_t x);
b128_t rf_b128_copysign(b128_t x, b128_t y);
b128_t rf_b128_min(b128_t x, b128_t y);
b128_t rf_b128_max(b128_t x, b128_t y);

// Rounding
b128_t rf_b128_ceil(b128_t x);
b128_t rf_b128_floor(b128_t x);
b128_t rf_b128_trunc(b128_t x);
b128_t rf_b128_round(b128_t x);

// Square root and FMA
b128_t rf_b128_sqrt(b128_t x);
b128_t rf_b128_fma(b128_t x, b128_t y, b128_t z);
b128_t rf_b128_fmod(b128_t x, b128_t y);

// Classification predicates
int32_t rf_b128_isnan(b128_t x);
int32_t rf_b128_isinf(b128_t x);
int32_t rf_b128_isfinite(b128_t x);
int32_t rf_b128_isnormal(b128_t x);
int32_t rf_b128_iszero(b128_t x);
int32_t rf_b128_signbit(b128_t x);

// Special values
b128_t rf_b128_nan(void);
b128_t rf_b128_inf(void);
b128_t rf_b128_neg_inf(void);
b128_t rf_b128_epsilon(void);
b128_t rf_b128_min_positive(void);
b128_t rf_b128_max_value(void);

// Transcendental functions - full precision via LibBF
b128_t rf_b128_sin(b128_t x);
b128_t rf_b128_cos(b128_t x);
b128_t rf_b128_tan(b128_t x);
b128_t rf_b128_asin(b128_t x);
b128_t rf_b128_acos(b128_t x);
b128_t rf_b128_atan(b128_t x);
b128_t rf_b128_atan2(b128_t y, b128_t x);

// Hyperbolic functions
b128_t rf_b128_sinh(b128_t x);
b128_t rf_b128_cosh(b128_t x);
b128_t rf_b128_tanh(b128_t x);
b128_t rf_b128_asinh(b128_t x);
b128_t rf_b128_acosh(b128_t x);
b128_t rf_b128_atanh(b128_t x);

// Exponential and logarithmic functions
b128_t rf_b128_exp(b128_t x);
b128_t rf_b128_exp2(b128_t x);
b128_t rf_b128_expm1(b128_t x);
b128_t rf_b128_log(b128_t x);
b128_t rf_b128_log2(b128_t x);
b128_t rf_b128_log10(b128_t x);
b128_t rf_b128_log1p(b128_t x);

// Power functions
b128_t rf_b128_pow(b128_t base, b128_t exp);
b128_t rf_b128_cbrt(b128_t x);
b128_t rf_b128_hypot(b128_t x, b128_t y);

// Rounding functions
b128_t rf_b128_floor(b128_t x);
b128_t rf_b128_ceil(b128_t x);
b128_t rf_b128_trunc(b128_t x);
b128_t rf_b128_round(b128_t x);
b128_t rf_b128_fmod(b128_t x, b128_t y);

// ============================================================================
// RazorForge-callable ABI bridges
//
// Passing/returning b128_t by value does not match the scalar fp128 call ABI
// emitted by RazorForge codegen (SysV: SSE vs integer-register classing;
// Win64: xmm0 vs hidden sret return). These variants take b128 inputs as
// (low, high) u64 pairs and write b128 results through an out pointer —
// both forms have identical ABI on every supported platform.
// ============================================================================

void rf_b128_sin_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_cos_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_tan_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_asin_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_acos_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_atan_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_sinh_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_cosh_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_tanh_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_asinh_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_acosh_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_atanh_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_exp_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_exp2_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_expm1_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_log_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_log2_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_log10_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_log1p_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_cbrt_parts(uint64_t low, uint64_t high, b128_t* out);
void rf_b128_atan2_parts(uint64_t y_low, uint64_t y_high, uint64_t x_low, uint64_t x_high, b128_t* out);
void rf_b128_pow_parts(uint64_t base_low, uint64_t base_high, uint64_t exp_low, uint64_t exp_high, b128_t* out);
void rf_b128_hypot_parts(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high, b128_t* out);
void rf_b128_copysign_parts(uint64_t value_low, uint64_t value_high, uint64_t sign_low, uint64_t sign_high, b128_t* out);
uint64_t rf_format_B128_parts(uint64_t low, uint64_t high);
void rf_d32_to_b128_parts(uint32_t bits, b128_t* out);
void rf_d64_to_b128_parts(uint64_t bits, b128_t* out);
void rf_d128_to_b128_parts(uint64_t low, uint64_t high, b128_t* out);

// Extended C99/C23 libm (added for full B16/B32/B64 parity).
double rf_b64_exp10(double x);
double rf_b64_scalbn(double x, int64_t n);
int64_t rf_b64_ilogb(double x);
double rf_b64_nextafter(double x, double y);
double rf_b64_rint(double x);
double rf_b64_fdim(double x, double y);
double rf_b64_sinpi(double x);
double rf_b64_cospi(double x);
double rf_b64_tanpi(double x);
float rf_b32_erf(float x);
float rf_b32_erfc(float x);
float rf_b32_tgamma(float x);
float rf_b32_lgamma(float x);
float rf_b32_exp10(float x);
float rf_b32_scalbn(float x, int64_t n);
int64_t rf_b32_ilogb(float x);
float rf_b32_nextafter(float x, float y);
float rf_b32_rint(float x);
float rf_b32_fdim(float x, float y);
float rf_b32_sinpi(float x);
float rf_b32_cospi(float x);
float rf_b32_tanpi(float x);

#ifdef __cplusplus
}
#endif

#endif // RAZORFORGE_MATH_H
