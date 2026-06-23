/*
 * RazorForge Runtime - Math Functions
 * cmath-compliant wrappers for floating point operations
 */

#include <math.h>
#include <stdint.h>
#include "../include/razorforge_math.h"

// ============================================================================
// f32 (float) math functions - wrapping cmath functions
// ============================================================================

float rf_f32_sin(float x) { return sinf(x); }
float rf_f32_cos(float x) { return cosf(x); }
float rf_f32_tan(float x) { return tanf(x); }
float rf_f32_asin(float x) { return asinf(x); }
float rf_f32_acos(float x) { return acosf(x); }
float rf_f32_atan(float x) { return atanf(x); }
float rf_f32_atan2(float y, float x) { return atan2f(y, x); }
float rf_f32_sinh(float x) { return sinhf(x); }
float rf_f32_cosh(float x) { return coshf(x); }
float rf_f32_tanh(float x) { return tanhf(x); }
float rf_f32_asinh(float x) { return asinhf(x); }
float rf_f32_acosh(float x) { return acoshf(x); }
float rf_f32_atanh(float x) { return atanhf(x); }
float rf_f32_exp(float x) { return expf(x); }
float rf_f32_exp2(float x) { return exp2f(x); }
float rf_f32_expm1(float x) { return expm1f(x); }
float rf_f32_log(float x) { return logf(x); }
float rf_f32_log2(float x) { return log2f(x); }
float rf_f32_log10(float x) { return log10f(x); }
float rf_f32_log1p(float x) { return log1pf(x); }
float rf_f32_pow(float base, float exp) { return powf(base, exp); }
float rf_f32_sqrt(float x) { return sqrtf(x); }
float rf_f32_cbrt(float x) { return cbrtf(x); }
float rf_f32_hypot(float x, float y) { return hypotf(x, y); }
float rf_f32_ceil(float x) { return ceilf(x); }
float rf_f32_floor(float x) { return floorf(x); }
float rf_f32_trunc(float x) { return truncf(x); }
float rf_f32_round(float x) { return roundf(x); }
float rf_f32_fabs(float x) { return fabsf(x); }
float rf_f32_fmod(float x, float y) { return fmodf(x, y); }
float rf_f32_remainder(float x, float y) { return remainderf(x, y); }
float rf_f32_fma(float x, float y, float z) { return fmaf(x, y, z); }
float rf_f32_fmin(float x, float y) { return fminf(x, y); }
float rf_f32_fmax(float x, float y) { return fmaxf(x, y); }
float rf_f32_copysign(float x, float y) { return copysignf(x, y); }
int32_t rf_f32_isnan(float x) { return isnan(x) ? 1 : 0; }
int32_t rf_f32_isinf(float x) { return isinf(x) ? 1 : 0; }
int32_t rf_f32_isfinite(float x) { return isfinite(x) ? 1 : 0; }
int32_t rf_f32_isnormal(float x) { return isnormal(x) ? 1 : 0; }
int32_t rf_f32_signbit(float x) { return signbit(x) ? 1 : 0; }

// ============================================================================
// f64 (double) math functions - wrapping cmath functions
// ============================================================================

double rf_f64_sin(double x) { return sin(x); }
double rf_f64_cos(double x) { return cos(x); }
double rf_f64_tan(double x) { return tan(x); }
double rf_f64_asin(double x) { return asin(x); }
double rf_f64_acos(double x) { return acos(x); }
double rf_f64_atan(double x) { return atan(x); }
double rf_f64_atan2(double y, double x) { return atan2(y, x); }
double rf_f64_sinh(double x) { return sinh(x); }
double rf_f64_cosh(double x) { return cosh(x); }
double rf_f64_tanh(double x) { return tanh(x); }
double rf_f64_asinh(double x) { return asinh(x); }
double rf_f64_acosh(double x) { return acosh(x); }
double rf_f64_atanh(double x) { return atanh(x); }
double rf_f64_exp(double x) { return exp(x); }
double rf_f64_exp2(double x) { return exp2(x); }
double rf_f64_expm1(double x) { return expm1(x); }
double rf_f64_log(double x) { return log(x); }
double rf_f64_log2(double x) { return log2(x); }
double rf_f64_log10(double x) { return log10(x); }
double rf_f64_log1p(double x) { return log1p(x); }
double rf_f64_pow(double base, double exp) { return pow(base, exp); }
double rf_f64_sqrt(double x) { return sqrt(x); }
double rf_f64_cbrt(double x) { return cbrt(x); }
double rf_f64_hypot(double x, double y) { return hypot(x, y); }
double rf_f64_ceil(double x) { return ceil(x); }
double rf_f64_floor(double x) { return floor(x); }
double rf_f64_trunc(double x) { return trunc(x); }
double rf_f64_round(double x) { return round(x); }
double rf_f64_fabs(double x) { return fabs(x); }
double rf_f64_fmod(double x, double y) { return fmod(x, y); }
double rf_f64_remainder(double x, double y) { return remainder(x, y); }
double rf_f64_fma(double x, double y, double z) { return fma(x, y, z); }
double rf_f64_fmin(double x, double y) { return fmin(x, y); }
double rf_f64_fmax(double x, double y) { return fmax(x, y); }
double rf_f64_copysign(double x, double y) { return copysign(x, y); }
int32_t rf_f64_isnan(double x) { return isnan(x) ? 1 : 0; }
int32_t rf_f64_isinf(double x) { return isinf(x) ? 1 : 0; }
int32_t rf_f64_isfinite(double x) { return isfinite(x) ? 1 : 0; }
int32_t rf_f64_isnormal(double x) { return isnormal(x) ? 1 : 0; }
int32_t rf_f64_signbit(double x) { return signbit(x) ? 1 : 0; }

// ============================================================================
// Type conversions - f32/f64
// ============================================================================

double rf_f32_to_f64(float x) { return (double)x; }
float rf_f64_to_f32(double x) { return (float)x; }

// ============================================================================
// Extended C99/C23 libm: sinpi/cospi/tanpi, exp10, scalbn, ilogb, nextafter,
// rint, fdim (f64 + f32), plus f32 erf/erfc/tgamma/lgamma. frexp/modf are
// composed RF-side (from ilogb+scalbn / trunc) to avoid pointer-return ABI.
// ============================================================================

#define RF_PI 3.14159265358979323846

// --- f64 ---
double rf_f64_exp10(double x) { return pow(10.0, x); }
double rf_f64_scalbn(double x, int64_t n) { return scalbn(x, (int)n); }
int64_t rf_f64_ilogb(double x) { return (int64_t)ilogb(x); }
double rf_f64_nextafter(double x, double y) { return nextafter(x, y); }
double rf_f64_rint(double x) { return rint(x); }
double rf_f64_fdim(double x, double y) { return fdim(x, y); }
// sinpi/cospi/tanpi: argument reduction via floor/fmod (no integer cast -> no
// overflow for large x), exact zeros at integers/half-integers, [0,0.5]
// reflection for accuracy, and poles (cospi == 0) handled in tanpi.
double rf_f64_sinpi(double x) {
    if (isnan(x)) return x;
    if (isinf(x)) return (double)NAN;
    double s = 1.0;
    if (signbit(x)) { x = -x; s = -1.0; }            // sinpi is odd (keeps -0 sign)
    double n = floor(x);
    double f = x - n;                                // f in [0,1)
    if (f == 0.0) return copysign(0.0, s);           // exact zero at integers (odd-function sign only)
    double sign = (fmod(n, 2.0) == 1.0) ? -1.0 : 1.0; // (-1)^n, overflow-safe
    double r = (f == 0.5) ? 1.0
             : (f < 0.5)  ? sin(RF_PI * f)
                          : sin(RF_PI * (1.0 - f));
    return s * sign * r;
}
double rf_f64_cospi(double x) {
    if (isnan(x)) return x;
    if (isinf(x)) return (double)NAN;
    x = fabs(x);                                     // cospi is even
    double n = floor(x);
    double f = x - n;
    double sign = (fmod(n, 2.0) == 1.0) ? -1.0 : 1.0;
    double r;
    if (f == 0.0) r = 1.0;
    else if (f == 0.5) r = 0.0;
    else if (f < 0.5) r = cos(RF_PI * f);
    else r = -cos(RF_PI * (1.0 - f));
    return sign * r;
}
double rf_f64_tanpi(double x) {
    if (isnan(x)) return x;
    if (isinf(x)) return (double)NAN;
    double sp = rf_f64_sinpi(x);
    double cp = rf_f64_cospi(x);
    if (cp == 0.0) return copysign((double)INFINITY, sp); // pole: blow up toward sinpi's sign
    return sp / cp;
}

// --- f32 ---
float rf_f32_erf(float x) { return erff(x); }
float rf_f32_erfc(float x) { return erfcf(x); }
float rf_f32_tgamma(float x) { return tgammaf(x); }
float rf_f32_lgamma(float x) { return lgammaf(x); }
float rf_f32_exp10(float x) { return powf(10.0f, x); }
float rf_f32_scalbn(float x, int64_t n) { return scalbnf(x, (int)n); }
int64_t rf_f32_ilogb(float x) { return (int64_t)ilogbf(x); }
float rf_f32_nextafter(float x, float y) { return nextafterf(x, y); }
float rf_f32_rint(float x) { return rintf(x); }
float rf_f32_fdim(float x, float y) { return fdimf(x, y); }
float rf_f32_sinpi(float x) {
    if (isnan(x)) return x;
    if (isinf(x)) return (float)NAN;
    float s = 1.0f;
    if (signbit(x)) { x = -x; s = -1.0f; }
    float n = floorf(x);
    float f = x - n;
    if (f == 0.0f) return copysignf(0.0f, s);
    float sign = (fmodf(n, 2.0f) == 1.0f) ? -1.0f : 1.0f;
    float r = (f == 0.5f) ? 1.0f
            : (f < 0.5f)  ? sinf((float)RF_PI * f)
                          : sinf((float)RF_PI * (1.0f - f));
    return s * sign * r;
}
float rf_f32_cospi(float x) {
    if (isnan(x)) return x;
    if (isinf(x)) return (float)NAN;
    x = fabsf(x);
    float n = floorf(x);
    float f = x - n;
    float sign = (fmodf(n, 2.0f) == 1.0f) ? -1.0f : 1.0f;
    float r;
    if (f == 0.0f) r = 1.0f;
    else if (f == 0.5f) r = 0.0f;
    else if (f < 0.5f) r = cosf((float)RF_PI * f);
    else r = -cosf((float)RF_PI * (1.0f - f));
    return sign * r;
}
float rf_f32_tanpi(float x) {
    if (isnan(x)) return x;
    if (isinf(x)) return (float)NAN;
    float sp = rf_f32_sinpi(x);
    float cp = rf_f32_cospi(x);
    if (cp == 0.0f) return copysignf((float)INFINITY, sp);
    return sp / cp;
}
