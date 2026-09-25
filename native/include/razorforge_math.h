#ifndef RAZORFORGE_MATH_H
#define RAZORFORGE_MATH_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// ============================================================================
// cmath-compliant math functions for binary floating point types (B32/B64)
//
// Thin wrappers over the platform libm. Every other numeric type (B16, B128, D32/D64/D128,
// Integer, Real, Decimal) is implemented in pure RazorForge, not in this runtime.
// ============================================================================

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
