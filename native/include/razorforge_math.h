#ifndef RAZORFORGE_MATH_H
#define RAZORFORGE_MATH_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// ============================================================================
// Platform libm wrappers for the B32/B64 operations the stdlib still binds.
//
// The transcendental functions of every binary float type are correctly rounded CORE-MATH ports
// in pure RazorForge, and every other numeric type (B128, D32/D64/D128, Integer, Real, Decimal)
// is pure RazorForge too. Only these exact operations and the b64 cbrt seed remain here.
// ============================================================================

// b32 (float)
float rf_b32_remainder(float x, float y);
float rf_b32_fdim(float x, float y);
float rf_b32_nextafter(float x, float y);
float rf_b32_scalbn(float x, int64_t n);
int64_t rf_b32_ilogb(float x);

// b64 (double)
double rf_b64_cbrt(double x);
double rf_b64_remainder(double x, double y);
double rf_b64_fdim(double x, double y);
double rf_b64_nextafter(double x, double y);
double rf_b64_scalbn(double x, int64_t n);
int64_t rf_b64_ilogb(double x);

#ifdef __cplusplus
}
#endif

#endif // RAZORFORGE_MATH_H
