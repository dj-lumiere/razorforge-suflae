/*
 * RazorForge Runtime - Math Functions
 * Platform libm wrappers for the few B32/B64 operations the stdlib still binds.
 *
 * Every transcendental function is a correctly rounded CORE-MATH port in pure RazorForge
 * (Standard/RazorForge/Core/Numerics/CoreMath/), and sqrt/fma/fmod/fmax/fmin/rint/trunc/...
 * lower to LLVM intrinsics. What is left here: the exact operations (remainder, fdim, nextafter,
 * scalbn, ilogb) and the b64 cbrt that B128/DoubleDouble use as a Newton seed.
 */

#include <math.h>
#include <stdint.h>
#include "../include/razorforge_math.h"

// --- b32 ---
float rf_b32_remainder(float x, float y) { return remainderf(x, y); }
float rf_b32_fdim(float x, float y) { return fdimf(x, y); }
float rf_b32_nextafter(float x, float y) { return nextafterf(x, y); }
float rf_b32_scalbn(float x, int64_t n) { return scalbnf(x, (int)n); }
int64_t rf_b32_ilogb(float x) { return (int64_t)ilogbf(x); }

// --- b64 ---
double rf_b64_cbrt(double x) { return cbrt(x); }
double rf_b64_remainder(double x, double y) { return remainder(x, y); }
double rf_b64_fdim(double x, double y) { return fdim(x, y); }
double rf_b64_nextafter(double x, double y) { return nextafter(x, y); }
double rf_b64_scalbn(double x, int64_t n) { return scalbn(x, (int)n); }
int64_t rf_b64_ilogb(double x) { return (int64_t)ilogb(x); }
