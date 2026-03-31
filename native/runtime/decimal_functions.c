/*
 * RazorForge Runtime - Decimal Floating Point Functions
 * IEEE 754-2008 decimal floating point operations using decNumber library
 *
 * decNumber provides:
 *   decSingle (D32)  - 7 significant digits   (no direct arithmetic)
 *   decDouble (D64)  - 16 significant digits
 *   decQuad   (D128) - 34 significant digits
 *
 * decSingle has NO arithmetic operations — all D32 ops widen to decDouble,
 * operate, then narrow back.
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

#ifdef HAVE_DECNUMBER

#define DECNUMDIGITS 34  // Must be >= 34 for decimal128 support
#include <decContext.h>
#include <decNumber.h>
#include <decimal32.h>
#include <decimal64.h>
#include <decimal128.h>
#include <decSingle.h>
#include <decDouble.h>
#include <decQuad.h>

// ============================================================================
// Type conversion helpers (raw integers <-> decNumber structs)
// ============================================================================

static inline decSingle to_single(uint32_t raw)
{
    decSingle s;
    memcpy(&s, &raw, sizeof(s));
    return s;
}

static inline uint32_t from_single(decSingle s)
{
    uint32_t raw;
    memcpy(&raw, &s, sizeof(raw));
    return raw;
}

static inline decDouble to_double(uint64_t raw)
{
    decDouble d;
    memcpy(&d, &raw, sizeof(d));
    return d;
}

static inline uint64_t from_double(decDouble d)
{
    uint64_t raw;
    memcpy(&raw, &d, sizeof(raw));
    return raw;
}

static inline decQuad to_quad(uint64_t low, uint64_t high)
{
    decQuad q;
    uint64_t parts[2] = { low, high };
    memcpy(&q, parts, sizeof(q));
    return q;
}

static inline d128_t from_quad(decQuad q)
{
    d128_t r;
    uint64_t parts[2];
    memcpy(parts, &q, sizeof(parts));
    r.low = parts[0];
    r.high = parts[1];
    return r;
}

// ============================================================================
// Thread-local contexts (one per format)
// ============================================================================

static decContext* get_ctx32(void)
{
    static _Thread_local decContext ctx;
    static _Thread_local int inited = 0;
    if (!inited) { decContextDefault(&ctx, DEC_INIT_DECIMAL32); ctx.traps = 0; inited = 1; }
    return &ctx;
}

static decContext* get_ctx64(void)
{
    static _Thread_local decContext ctx;
    static _Thread_local int inited = 0;
    if (!inited) { decContextDefault(&ctx, DEC_INIT_DECIMAL64); ctx.traps = 0; inited = 1; }
    return &ctx;
}

static decContext* get_ctx128(void)
{
    static _Thread_local decContext ctx;
    static _Thread_local int inited = 0;
    if (!inited) { decContextDefault(&ctx, DEC_INIT_DECIMAL128); ctx.traps = 0; inited = 1; }
    return &ctx;
}

// ============================================================================
// d32 operations (decimal32 - 7 significant digits)
// decSingle has NO arithmetic — widen to decDouble, operate, narrow back.
// ============================================================================

uint32_t rf_d32_add(uint32_t a, uint32_t b)
{
    decContext* ctx = get_ctx64();
    decDouble da, db, dr;
    decSingle sa = to_single(a), sb = to_single(b), sr;
    decSingleToWider(&sa, &da);
    decSingleToWider(&sb, &db);
    decDoubleAdd(&dr, &da, &db, ctx);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_sub(uint32_t a, uint32_t b)
{
    decContext* ctx = get_ctx64();
    decDouble da, db, dr;
    decSingle sa = to_single(a), sb = to_single(b), sr;
    decSingleToWider(&sa, &da);
    decSingleToWider(&sb, &db);
    decDoubleSubtract(&dr, &da, &db, ctx);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_mul(uint32_t a, uint32_t b)
{
    decContext* ctx = get_ctx64();
    decDouble da, db, dr;
    decSingle sa = to_single(a), sb = to_single(b), sr;
    decSingleToWider(&sa, &da);
    decSingleToWider(&sb, &db);
    decDoubleMultiply(&dr, &da, &db, ctx);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_div(uint32_t a, uint32_t b)
{
    decContext* ctx = get_ctx64();
    decDouble da, db, dr;
    decSingle sa = to_single(a), sb = to_single(b), sr;
    decSingleToWider(&sa, &da);
    decSingleToWider(&sb, &db);
    decDoubleDivide(&dr, &da, &db, ctx);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

int32_t rf_d32_cmp(uint32_t a, uint32_t b)
{
    decContext* ctx = get_ctx64();
    decDouble da, db, dr;
    decSingle sa = to_single(a), sb = to_single(b);
    decSingleToWider(&sa, &da);
    decSingleToWider(&sb, &db);
    decDoubleCompare(&dr, &da, &db, ctx);
    if (decDoubleIsNegative(&dr)) return -1;
    if (decDoubleIsZero(&dr)) return 0;
    return 1;
}

uint32_t rf_d32_neg(uint32_t a)
{
    decDouble da, dr;
    decSingle sa = to_single(a), sr;
    decSingleToWider(&sa, &da);
    decDoubleCopyNegate(&dr, &da);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_from_string(const char* str)
{
    decContext* ctx = get_ctx32();
    decSingle s;
    decSingleFromString(&s, str, ctx);
    return from_single(s);
}

char* rf_d32_to_string(uint32_t val)
{
    char* buf = (char*)malloc(64);
    decSingle s = to_single(val);
    decSingleToString(&s, buf);
    return buf;
}

uint32_t rf_d32_from_s32(int32_t val)
{
    // Go through decDouble (has FromInt32) then narrow
    decContext* ctx = get_ctx64();
    decDouble dd;
    decSingle sr;
    decDoubleFromInt32(&dd, val);
    decSingleFromWider(&sr, &dd, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_from_s64(int64_t val)
{
    // Convert via string for 64-bit values
    char buf[32];
    snprintf(buf, sizeof(buf), "%lld", (long long)val);
    return rf_d32_from_string(buf);
}

uint32_t rf_d32_from_u32(uint32_t val)
{
    decContext* ctx = get_ctx64();
    decDouble dd;
    decSingle sr;
    decDoubleFromUInt32(&dd, val);
    decSingleFromWider(&sr, &dd, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_from_u64(uint64_t val)
{
    char buf[32];
    snprintf(buf, sizeof(buf), "%llu", (unsigned long long)val);
    return rf_d32_from_string(buf);
}

int32_t rf_d32_to_s32(uint32_t val)
{
    decContext* ctx = get_ctx64();
    decDouble dd;
    decSingle s = to_single(val);
    decSingleToWider(&s, &dd);
    return decDoubleToInt32(&dd, ctx, DEC_ROUND_DOWN);
}

int64_t rf_d32_to_s64(uint32_t val)
{
    // Convert via string
    char buf[64];
    decSingle s = to_single(val);
    decSingleToString(&s, buf);
    return strtoll(buf, NULL, 10);
}

// ============================================================================
// d64 operations (decimal64 - 16 significant digits)
// ============================================================================

uint64_t rf_d64_add(uint64_t a, uint64_t b)
{
    decContext* ctx = get_ctx64();
    decDouble da = to_double(a), db = to_double(b), dr;
    decDoubleAdd(&dr, &da, &db, ctx);
    return from_double(dr);
}

uint64_t rf_d64_sub(uint64_t a, uint64_t b)
{
    decContext* ctx = get_ctx64();
    decDouble da = to_double(a), db = to_double(b), dr;
    decDoubleSubtract(&dr, &da, &db, ctx);
    return from_double(dr);
}

uint64_t rf_d64_mul(uint64_t a, uint64_t b)
{
    decContext* ctx = get_ctx64();
    decDouble da = to_double(a), db = to_double(b), dr;
    decDoubleMultiply(&dr, &da, &db, ctx);
    return from_double(dr);
}

uint64_t rf_d64_div(uint64_t a, uint64_t b)
{
    decContext* ctx = get_ctx64();
    decDouble da = to_double(a), db = to_double(b), dr;
    decDoubleDivide(&dr, &da, &db, ctx);
    return from_double(dr);
}

int32_t rf_d64_cmp(uint64_t a, uint64_t b)
{
    decContext* ctx = get_ctx64();
    decDouble da = to_double(a), db = to_double(b), dr;
    decDoubleCompare(&dr, &da, &db, ctx);
    if (decDoubleIsNegative(&dr)) return -1;
    if (decDoubleIsZero(&dr)) return 0;
    return 1;
}

uint64_t rf_d64_neg(uint64_t a)
{
    decDouble da = to_double(a), dr;
    decDoubleCopyNegate(&dr, &da);
    return from_double(dr);
}

uint64_t rf_d64_from_string(const char* str)
{
    decContext* ctx = get_ctx64();
    decDouble d;
    decDoubleFromString(&d, str, ctx);
    return from_double(d);
}

char* rf_d64_to_string(uint64_t val)
{
    char* buf = (char*)malloc(64);
    decDouble d = to_double(val);
    decDoubleToString(&d, buf);
    return buf;
}

uint64_t rf_d64_from_s32(int32_t val)
{
    decDouble d;
    decDoubleFromInt32(&d, val);
    return from_double(d);
}

uint64_t rf_d64_from_s64(int64_t val)
{
    char buf[32];
    snprintf(buf, sizeof(buf), "%lld", (long long)val);
    return rf_d64_from_string(buf);
}

uint64_t rf_d64_from_u32(uint32_t val)
{
    decDouble d;
    decDoubleFromUInt32(&d, val);
    return from_double(d);
}

uint64_t rf_d64_from_u64(uint64_t val)
{
    char buf[32];
    snprintf(buf, sizeof(buf), "%llu", (unsigned long long)val);
    return rf_d64_from_string(buf);
}

int32_t rf_d64_to_s32(uint64_t val)
{
    decContext* ctx = get_ctx64();
    decDouble d = to_double(val);
    return decDoubleToInt32(&d, ctx, DEC_ROUND_DOWN);
}

int64_t rf_d64_to_s64(uint64_t val)
{
    char buf[64];
    decDouble d = to_double(val);
    decDoubleToString(&d, buf);
    return strtoll(buf, NULL, 10);
}

// ============================================================================
// d128 operations (decimal128 - 34 significant digits)
//
// All public d128 functions take split (uint64_t low, uint64_t high) params
// to avoid LLVM {i64,i64} vs MSVC struct-by-pointer ABI mismatch.
// ============================================================================

d128_t rf_d128_add(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    decContext* ctx = get_ctx128();
    decQuad qa = to_quad(a_low, a_high), qb = to_quad(b_low, b_high), qr;
    decQuadAdd(&qr, &qa, &qb, ctx);
    return from_quad(qr);
}

d128_t rf_d128_sub(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    decContext* ctx = get_ctx128();
    decQuad qa = to_quad(a_low, a_high), qb = to_quad(b_low, b_high), qr;
    decQuadSubtract(&qr, &qa, &qb, ctx);
    return from_quad(qr);
}

d128_t rf_d128_mul(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    decContext* ctx = get_ctx128();
    decQuad qa = to_quad(a_low, a_high), qb = to_quad(b_low, b_high), qr;
    decQuadMultiply(&qr, &qa, &qb, ctx);
    return from_quad(qr);
}

d128_t rf_d128_div(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    decContext* ctx = get_ctx128();
    decQuad qa = to_quad(a_low, a_high), qb = to_quad(b_low, b_high), qr;
    decQuadDivide(&qr, &qa, &qb, ctx);
    return from_quad(qr);
}

int32_t rf_d128_cmp(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    decContext* ctx = get_ctx128();
    decQuad qa = to_quad(a_low, a_high), qb = to_quad(b_low, b_high), qr;
    decQuadCompare(&qr, &qa, &qb, ctx);
    if (decQuadIsNegative(&qr)) return -1;
    if (decQuadIsZero(&qr)) return 0;
    return 1;
}

d128_t rf_d128_neg(uint64_t a_low, uint64_t a_high)
{
    decQuad qa = to_quad(a_low, a_high), qr;
    decQuadCopyNegate(&qr, &qa);
    return from_quad(qr);
}

d128_t rf_d128_from_string(const char* str)
{
    decContext* ctx = get_ctx128();
    decQuad q;
    decQuadFromString(&q, str, ctx);
    return from_quad(q);
}

char* rf_d128_to_string(uint64_t low, uint64_t high)
{
    char* buf = (char*)malloc(128);
    decQuad q = to_quad(low, high);
    decQuadToString(&q, buf);
    return buf;
}

d128_t rf_d128_from_s32(int32_t val)
{
    // Go through decQuad via string (decQuad has FromInt32 on some versions)
    char buf[32];
    snprintf(buf, sizeof(buf), "%d", val);
    return rf_d128_from_string(buf);
}

d128_t rf_d128_from_s64(int64_t val)
{
    char buf[32];
    snprintf(buf, sizeof(buf), "%lld", (long long)val);
    return rf_d128_from_string(buf);
}

d128_t rf_d128_from_u32(uint32_t val)
{
    char buf[32];
    snprintf(buf, sizeof(buf), "%u", val);
    return rf_d128_from_string(buf);
}

d128_t rf_d128_from_u64(uint64_t val)
{
    char buf[32];
    snprintf(buf, sizeof(buf), "%llu", (unsigned long long)val);
    return rf_d128_from_string(buf);
}

int32_t rf_d128_to_s32(uint64_t low, uint64_t high)
{
    decContext* ctx = get_ctx128();
    decQuad q = to_quad(low, high);
    return decQuadToInt32(&q, ctx, DEC_ROUND_DOWN);
}

int64_t rf_d128_to_s64(uint64_t low, uint64_t high)
{
    char buf[128];
    decQuad q = to_quad(low, high);
    decQuadToString(&q, buf);
    return strtoll(buf, NULL, 10);
}

// ============================================================================
// Cross-format conversions
// ============================================================================

uint64_t rf_d32_to_d64(uint32_t x)
{
    decDouble dd;
    decSingle s = to_single(x);
    decSingleToWider(&s, &dd);
    return from_double(dd);
}

d128_t rf_d32_to_d128(uint32_t x)
{
    decDouble dd;
    decQuad qq;
    decSingle s = to_single(x);
    decSingleToWider(&s, &dd);
    decDoubleToWider(&dd, &qq);
    return from_quad(qq);
}

uint32_t rf_d64_to_d32(uint64_t x)
{
    decSingle sr;
    decDouble dd = to_double(x);
    decSingleFromWider(&sr, &dd, get_ctx32());
    return from_single(sr);
}

d128_t rf_d64_to_d128(uint64_t x)
{
    decQuad qq;
    decDouble dd = to_double(x);
    decDoubleToWider(&dd, &qq);
    return from_quad(qq);
}

uint32_t rf_d128_to_d32(uint64_t x_low, uint64_t x_high)
{
    decQuad qq = to_quad(x_low, x_high);
    decDouble dd;
    decSingle sr;
    decDoubleFromWider(&dd, &qq, get_ctx64());
    decSingleFromWider(&sr, &dd, get_ctx32());
    return from_single(sr);
}

uint64_t rf_d128_to_d64(uint64_t x_low, uint64_t x_high)
{
    decQuad qq = to_quad(x_low, x_high);
    decDouble dd;
    decDoubleFromWider(&dd, &qq, get_ctx64());
    return from_double(dd);
}

// ============================================================================
// Binary float <-> decimal conversions (via string roundtrip)
// ============================================================================

uint32_t rf_f32_to_d32(float x)
{
    char buf[64];
    snprintf(buf, sizeof(buf), "%.9g", (double)x);
    return rf_d32_from_string(buf);
}

uint64_t rf_f32_to_d64(float x)
{
    char buf[64];
    snprintf(buf, sizeof(buf), "%.9g", (double)x);
    return rf_d64_from_string(buf);
}

d128_t rf_f32_to_d128(float x)
{
    char buf[64];
    snprintf(buf, sizeof(buf), "%.9g", (double)x);
    return rf_d128_from_string(buf);
}

uint32_t rf_f64_to_d32(double x)
{
    char buf[64];
    snprintf(buf, sizeof(buf), "%.17g", x);
    return rf_d32_from_string(buf);
}

uint64_t rf_f64_to_d64(double x)
{
    char buf[64];
    snprintf(buf, sizeof(buf), "%.17g", x);
    return rf_d64_from_string(buf);
}

d128_t rf_f64_to_d128(double x)
{
    char buf[64];
    snprintf(buf, sizeof(buf), "%.17g", x);
    return rf_d128_from_string(buf);
}

float rf_d32_to_f32(uint32_t x)
{
    char buf[64];
    decSingle s = to_single(x);
    decSingleToString(&s, buf);
    return strtof(buf, NULL);
}

double rf_d32_to_f64(uint32_t x)
{
    char buf[64];
    decSingle s = to_single(x);
    decSingleToString(&s, buf);
    return strtod(buf, NULL);
}

float rf_d64_to_f32(uint64_t x)
{
    char buf[64];
    decDouble d = to_double(x);
    decDoubleToString(&d, buf);
    return strtof(buf, NULL);
}

double rf_d64_to_f64(uint64_t x)
{
    char buf[64];
    decDouble d = to_double(x);
    decDoubleToString(&d, buf);
    return strtod(buf, NULL);
}

float rf_d128_to_f32(uint64_t x_low, uint64_t x_high)
{
    char buf[128];
    decQuad q = to_quad(x_low, x_high);
    decQuadToString(&q, buf);
    return strtof(buf, NULL);
}

double rf_d128_to_f64(uint64_t x_low, uint64_t x_high)
{
    char buf[128];
    decQuad q = to_quad(x_low, x_high);
    decQuadToString(&q, buf);
    return strtod(buf, NULL);
}

// ============================================================================
// d128 <-> f128 conversion (via string roundtrip)
// ============================================================================

f128_t rf_d128_to_f128(uint64_t x_low, uint64_t x_high)
{
    // Convert d128 to string, then parse as f128
    char buf[128];
    decQuad q = to_quad(x_low, x_high);
    decQuadToString(&q, buf);
    return rf_f128_from_string(buf);
}

d128_t rf_f128_to_d128(f128_t x)
{
    char* buf = rf_f128_to_string(x);
    d128_t result = rf_d128_from_string(buf);
    free(buf);
    return result;
}

// ============================================================================
// d128 transcendental functions via f128 path
//
// Strategy: d128 -> f128 -> LibBF transcendental -> f128 -> d128
// LibBF provides arbitrary precision transcendentals with exact rounding.
// Both d128 and f128 have ~34 significant digits.
// ============================================================================

d128_t rf_d128_sin(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_sin(f));
}

d128_t rf_d128_cos(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_cos(f));
}

d128_t rf_d128_tan(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_tan(f));
}

d128_t rf_d128_asin(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_asin(f));
}

d128_t rf_d128_acos(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_acos(f));
}

d128_t rf_d128_atan(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_atan(f));
}

d128_t rf_d128_atan2(uint64_t y_low, uint64_t y_high, uint64_t x_low, uint64_t x_high)
{
    f128_t fy = rf_d128_to_f128(y_low, y_high);
    f128_t fx = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_atan2(fy, fx));
}

d128_t rf_d128_sinh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_sinh(f));
}

d128_t rf_d128_cosh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_cosh(f));
}

d128_t rf_d128_tanh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_tanh(f));
}

d128_t rf_d128_asinh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_asinh(f));
}

d128_t rf_d128_acosh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_acosh(f));
}

d128_t rf_d128_atanh(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_atanh(f));
}

d128_t rf_d128_exp(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_exp(f));
}

d128_t rf_d128_exp2(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_exp2(f));
}

d128_t rf_d128_expm1(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_expm1(f));
}

d128_t rf_d128_log(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_log(f));
}

d128_t rf_d128_log2(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_log2(f));
}

d128_t rf_d128_log10(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_log10(f));
}

d128_t rf_d128_log1p(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_log1p(f));
}

d128_t rf_d128_pow(uint64_t base_low, uint64_t base_high, uint64_t exp_low, uint64_t exp_high)
{
    f128_t fb = rf_d128_to_f128(base_low, base_high);
    f128_t fe = rf_d128_to_f128(exp_low, exp_high);
    return rf_f128_to_d128(rf_f128_pow(fb, fe));
}

d128_t rf_d128_cbrt(uint64_t x_low, uint64_t x_high)
{
    f128_t f = rf_d128_to_f128(x_low, x_high);
    return rf_f128_to_d128(rf_f128_cbrt(f));
}

d128_t rf_d128_hypot(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high)
{
    f128_t fx = rf_d128_to_f128(x_low, x_high);
    f128_t fy = rf_d128_to_f128(y_low, y_high);
    return rf_f128_to_d128(rf_f128_hypot(fx, fy));
}

// ============================================================================
// d32 math functions
// ============================================================================

uint32_t rf_d32_sqrt(uint32_t x)
{
    // Widen to d64, use decDouble (no sqrt on decDouble either — go via decNumber)
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL64);
    ctx.traps = 0;
    decDouble dd;
    decSingle s = to_single(x);
    decSingleToWider(&s, &dd);
    decNumber dn, dr;
    decDoubleToNumber(&dd, &dn);
    decNumberSquareRoot(&dr, &dn, &ctx);
    decDoubleFromNumber(&dd, &dr, &ctx);
    decSingle sr;
    decSingleFromWider(&sr, &dd, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_abs(uint32_t x)
{
    decDouble dd, dr;
    decSingle s = to_single(x), sr;
    decSingleToWider(&s, &dd);
    decDoubleCopyAbs(&dr, &dd);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_ceil(uint32_t x)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL64);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_CEILING;
    decDouble dd, dr;
    decSingle s = to_single(x), sr;
    decSingleToWider(&s, &dd);
    decDoubleToIntegralValue(&dr, &dd, &ctx, ctx.round);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_floor(uint32_t x)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL64);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_FLOOR;
    decDouble dd, dr;
    decSingle s = to_single(x), sr;
    decSingleToWider(&s, &dd);
    decDoubleToIntegralValue(&dr, &dd, &ctx, ctx.round);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_round(uint32_t x)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL64);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_HALF_UP;
    decDouble dd, dr;
    decSingle s = to_single(x), sr;
    decSingleToWider(&s, &dd);
    decDoubleToIntegralValue(&dr, &dd, &ctx, ctx.round);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_trunc(uint32_t x)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL64);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_DOWN;
    decDouble dd, dr;
    decSingle s = to_single(x), sr;
    decSingleToWider(&s, &dd);
    decDoubleToIntegralValue(&dr, &dd, &ctx, ctx.round);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_fmod(uint32_t x, uint32_t y)
{
    decContext* ctx = get_ctx64();
    decDouble dx, dy, dr;
    decSingle sx = to_single(x), sy = to_single(y), sr;
    decSingleToWider(&sx, &dx);
    decSingleToWider(&sy, &dy);
    decDoubleRemainder(&dr, &dx, &dy, ctx);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_fma(uint32_t x, uint32_t y, uint32_t z)
{
    decContext* ctx = get_ctx64();
    decDouble dx, dy, dz, dr;
    decSingle sx = to_single(x), sy = to_single(y), sz = to_single(z), sr;
    decSingleToWider(&sx, &dx);
    decSingleToWider(&sy, &dy);
    decSingleToWider(&sz, &dz);
    decDoubleFMA(&dr, &dx, &dy, &dz, ctx);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_min(uint32_t x, uint32_t y)
{
    decContext* ctx = get_ctx64();
    decDouble dx, dy, dr;
    decSingle sx = to_single(x), sy = to_single(y), sr;
    decSingleToWider(&sx, &dx);
    decSingleToWider(&sy, &dy);
    decDoubleMin(&dr, &dx, &dy, ctx);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

uint32_t rf_d32_max(uint32_t x, uint32_t y)
{
    decContext* ctx = get_ctx64();
    decDouble dx, dy, dr;
    decSingle sx = to_single(x), sy = to_single(y), sr;
    decSingleToWider(&sx, &dx);
    decSingleToWider(&sy, &dy);
    decDoubleMax(&dr, &dx, &dy, ctx);
    decSingleFromWider(&sr, &dr, get_ctx32());
    return from_single(sr);
}

// decSingle has NO classification functions — widen to decDouble for queries
int32_t rf_d32_isnan(uint32_t x)
{
    decDouble dd; decSingle s = to_single(x);
    decSingleToWider(&s, &dd);
    return decDoubleIsNaN(&dd) ? 1 : 0;
}

int32_t rf_d32_isinf(uint32_t x)
{
    decDouble dd; decSingle s = to_single(x);
    decSingleToWider(&s, &dd);
    return decDoubleIsInfinite(&dd) ? 1 : 0;
}

int32_t rf_d32_isfinite(uint32_t x)
{
    decDouble dd; decSingle s = to_single(x);
    decSingleToWider(&s, &dd);
    return decDoubleIsFinite(&dd) ? 1 : 0;
}

int32_t rf_d32_isnormal(uint32_t x)
{
    decDouble dd; decSingle s = to_single(x);
    decSingleToWider(&s, &dd);
    return decDoubleIsNormal(&dd) ? 1 : 0;
}

int32_t rf_d32_iszero(uint32_t x)
{
    decDouble dd; decSingle s = to_single(x);
    decSingleToWider(&s, &dd);
    return decDoubleIsZero(&dd) ? 1 : 0;
}

int32_t rf_d32_signbit(uint32_t x)
{
    decDouble dd; decSingle s = to_single(x);
    decSingleToWider(&s, &dd);
    return decDoubleIsSigned(&dd) ? 1 : 0;
}

// ============================================================================
// d64 math functions
// ============================================================================

uint64_t rf_d64_sqrt(uint64_t x)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL64);
    ctx.traps = 0;
    decDouble dd = to_double(x), dr;
    decNumber dn, dnr;
    decDoubleToNumber(&dd, &dn);
    decNumberSquareRoot(&dnr, &dn, &ctx);
    decDoubleFromNumber(&dr, &dnr, &ctx);
    return from_double(dr);
}

uint64_t rf_d64_abs(uint64_t x)
{
    decDouble d = to_double(x), r;
    decDoubleCopyAbs(&r, &d);
    return from_double(r);
}

uint64_t rf_d64_ceil(uint64_t x)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL64);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_CEILING;
    decDouble d = to_double(x), r;
    decDoubleToIntegralValue(&r, &d, &ctx, ctx.round);
    return from_double(r);
}

uint64_t rf_d64_floor(uint64_t x)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL64);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_FLOOR;
    decDouble d = to_double(x), r;
    decDoubleToIntegralValue(&r, &d, &ctx, ctx.round);
    return from_double(r);
}

uint64_t rf_d64_round(uint64_t x)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL64);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_HALF_UP;
    decDouble d = to_double(x), r;
    decDoubleToIntegralValue(&r, &d, &ctx, ctx.round);
    return from_double(r);
}

uint64_t rf_d64_trunc(uint64_t x)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL64);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_DOWN;
    decDouble d = to_double(x), r;
    decDoubleToIntegralValue(&r, &d, &ctx, ctx.round);
    return from_double(r);
}

uint64_t rf_d64_fmod(uint64_t x, uint64_t y)
{
    decContext* ctx = get_ctx64();
    decDouble dx = to_double(x), dy = to_double(y), dr;
    decDoubleRemainder(&dr, &dx, &dy, ctx);
    return from_double(dr);
}

uint64_t rf_d64_fma(uint64_t x, uint64_t y, uint64_t z)
{
    decContext* ctx = get_ctx64();
    decDouble dx = to_double(x), dy = to_double(y), dz = to_double(z), dr;
    decDoubleFMA(&dr, &dx, &dy, &dz, ctx);
    return from_double(dr);
}

uint64_t rf_d64_min(uint64_t x, uint64_t y)
{
    decContext* ctx = get_ctx64();
    decDouble dx = to_double(x), dy = to_double(y), dr;
    decDoubleMin(&dr, &dx, &dy, ctx);
    return from_double(dr);
}

uint64_t rf_d64_max(uint64_t x, uint64_t y)
{
    decContext* ctx = get_ctx64();
    decDouble dx = to_double(x), dy = to_double(y), dr;
    decDoubleMax(&dr, &dx, &dy, ctx);
    return from_double(dr);
}

int32_t rf_d64_isnan(uint64_t x)
{
    decDouble d = to_double(x);
    return decDoubleIsNaN(&d) ? 1 : 0;
}

int32_t rf_d64_isinf(uint64_t x)
{
    decDouble d = to_double(x);
    return decDoubleIsInfinite(&d) ? 1 : 0;
}

int32_t rf_d64_isfinite(uint64_t x)
{
    decDouble d = to_double(x);
    return decDoubleIsFinite(&d) ? 1 : 0;
}

int32_t rf_d64_isnormal(uint64_t x)
{
    decDouble d = to_double(x);
    return decDoubleIsNormal(&d) ? 1 : 0;
}

int32_t rf_d64_iszero(uint64_t x)
{
    decDouble d = to_double(x);
    return decDoubleIsZero(&d) ? 1 : 0;
}

int32_t rf_d64_signbit(uint64_t x)
{
    decDouble d = to_double(x);
    return decDoubleIsSigned(&d) ? 1 : 0;
}

// ============================================================================
// d128 math functions
// ============================================================================

d128_t rf_d128_sqrt(uint64_t x_low, uint64_t x_high)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL128);
    ctx.traps = 0;
    decQuad q = to_quad(x_low, x_high), qr;
    decNumber dn, dnr;
    decQuadToNumber(&q, &dn);
    decNumberSquareRoot(&dnr, &dn, &ctx);
    decQuadFromNumber(&qr, &dnr, &ctx);
    return from_quad(qr);
}

d128_t rf_d128_abs(uint64_t x_low, uint64_t x_high)
{
    decQuad q = to_quad(x_low, x_high), r;
    decQuadCopyAbs(&r, &q);
    return from_quad(r);
}

d128_t rf_d128_ceil(uint64_t x_low, uint64_t x_high)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL128);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_CEILING;
    decQuad q = to_quad(x_low, x_high), r;
    decQuadToIntegralValue(&r, &q, &ctx, ctx.round);
    return from_quad(r);
}

d128_t rf_d128_floor(uint64_t x_low, uint64_t x_high)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL128);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_FLOOR;
    decQuad q = to_quad(x_low, x_high), r;
    decQuadToIntegralValue(&r, &q, &ctx, ctx.round);
    return from_quad(r);
}

d128_t rf_d128_round(uint64_t x_low, uint64_t x_high)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL128);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_HALF_UP;
    decQuad q = to_quad(x_low, x_high), r;
    decQuadToIntegralValue(&r, &q, &ctx, ctx.round);
    return from_quad(r);
}

d128_t rf_d128_trunc(uint64_t x_low, uint64_t x_high)
{
    decContext ctx;
    decContextDefault(&ctx, DEC_INIT_DECIMAL128);
    ctx.traps = 0;
    ctx.round = DEC_ROUND_DOWN;
    decQuad q = to_quad(x_low, x_high), r;
    decQuadToIntegralValue(&r, &q, &ctx, ctx.round);
    return from_quad(r);
}

d128_t rf_d128_fmod(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high)
{
    decContext* ctx = get_ctx128();
    decQuad qx = to_quad(x_low, x_high), qy = to_quad(y_low, y_high), qr;
    decQuadRemainder(&qr, &qx, &qy, ctx);
    return from_quad(qr);
}

d128_t rf_d128_fma(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high, uint64_t z_low, uint64_t z_high)
{
    decContext* ctx = get_ctx128();
    decQuad qx = to_quad(x_low, x_high), qy = to_quad(y_low, y_high), qz = to_quad(z_low, z_high), qr;
    decQuadFMA(&qr, &qx, &qy, &qz, ctx);
    return from_quad(qr);
}

d128_t rf_d128_min(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high)
{
    decContext* ctx = get_ctx128();
    decQuad qx = to_quad(x_low, x_high), qy = to_quad(y_low, y_high), qr;
    decQuadMin(&qr, &qx, &qy, ctx);
    return from_quad(qr);
}

d128_t rf_d128_max(uint64_t x_low, uint64_t x_high, uint64_t y_low, uint64_t y_high)
{
    decContext* ctx = get_ctx128();
    decQuad qx = to_quad(x_low, x_high), qy = to_quad(y_low, y_high), qr;
    decQuadMax(&qr, &qx, &qy, ctx);
    return from_quad(qr);
}

int32_t rf_d128_isnan(uint64_t x_low, uint64_t x_high)
{
    decQuad q = to_quad(x_low, x_high);
    return decQuadIsNaN(&q) ? 1 : 0;
}

int32_t rf_d128_isinf(uint64_t x_low, uint64_t x_high)
{
    decQuad q = to_quad(x_low, x_high);
    return decQuadIsInfinite(&q) ? 1 : 0;
}

int32_t rf_d128_isfinite(uint64_t x_low, uint64_t x_high)
{
    decQuad q = to_quad(x_low, x_high);
    return decQuadIsFinite(&q) ? 1 : 0;
}

int32_t rf_d128_isnormal(uint64_t x_low, uint64_t x_high)
{
    decQuad q = to_quad(x_low, x_high);
    return decQuadIsNormal(&q) ? 1 : 0;
}

int32_t rf_d128_iszero(uint64_t x_low, uint64_t x_high)
{
    decQuad q = to_quad(x_low, x_high);
    return decQuadIsZero(&q) ? 1 : 0;
}

int32_t rf_d128_signbit(uint64_t x_low, uint64_t x_high)
{
    decQuad q = to_quad(x_low, x_high);
    return decQuadIsSigned(&q) ? 1 : 0;
}

// ============================================================================
// Special values
// ============================================================================

uint32_t rf_d32_nan(void)
{
    return rf_d32_from_string("NaN");
}

uint32_t rf_d32_inf(void)
{
    return rf_d32_from_string("Infinity");
}

uint32_t rf_d32_neg_inf(void)
{
    return rf_d32_from_string("-Infinity");
}

uint64_t rf_d64_nan(void)
{
    return rf_d64_from_string("NaN");
}

uint64_t rf_d64_inf(void)
{
    return rf_d64_from_string("Infinity");
}

uint64_t rf_d64_neg_inf(void)
{
    return rf_d64_from_string("-Infinity");
}

d128_t rf_d128_nan(void)
{
    return rf_d128_from_string("NaN");
}

d128_t rf_d128_inf(void)
{
    return rf_d128_from_string("Infinity");
}

d128_t rf_d128_neg_inf(void)
{
    return rf_d128_from_string("-Infinity");
}

// ============================================================================
// Comparison predicates
// ============================================================================

int32_t rf_d32_eq(uint32_t a, uint32_t b)  { return rf_d32_cmp(a, b) == 0; }
int32_t rf_d32_ne(uint32_t a, uint32_t b)  { return rf_d32_cmp(a, b) != 0; }
int32_t rf_d32_lt(uint32_t a, uint32_t b)  { return rf_d32_cmp(a, b) < 0; }
int32_t rf_d32_le(uint32_t a, uint32_t b)  { return rf_d32_cmp(a, b) <= 0; }
int32_t rf_d32_gt(uint32_t a, uint32_t b)  { return rf_d32_cmp(a, b) > 0; }
int32_t rf_d32_ge(uint32_t a, uint32_t b)  { return rf_d32_cmp(a, b) >= 0; }

int32_t rf_d64_eq(uint64_t a, uint64_t b)  { return rf_d64_cmp(a, b) == 0; }
int32_t rf_d64_ne(uint64_t a, uint64_t b)  { return rf_d64_cmp(a, b) != 0; }
int32_t rf_d64_lt(uint64_t a, uint64_t b)  { return rf_d64_cmp(a, b) < 0; }
int32_t rf_d64_le(uint64_t a, uint64_t b)  { return rf_d64_cmp(a, b) <= 0; }
int32_t rf_d64_gt(uint64_t a, uint64_t b)  { return rf_d64_cmp(a, b) > 0; }
int32_t rf_d64_ge(uint64_t a, uint64_t b)  { return rf_d64_cmp(a, b) >= 0; }

int32_t rf_d128_eq(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return rf_d128_cmp(a_low, a_high, b_low, b_high) == 0;
}
int32_t rf_d128_ne(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return rf_d128_cmp(a_low, a_high, b_low, b_high) != 0;
}
int32_t rf_d128_lt(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return rf_d128_cmp(a_low, a_high, b_low, b_high) < 0;
}
int32_t rf_d128_le(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return rf_d128_cmp(a_low, a_high, b_low, b_high) <= 0;
}
int32_t rf_d128_gt(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return rf_d128_cmp(a_low, a_high, b_low, b_high) > 0;
}
int32_t rf_d128_ge(uint64_t a_low, uint64_t a_high, uint64_t b_low, uint64_t b_high)
{
    return rf_d128_cmp(a_low, a_high, b_low, b_high) >= 0;
}

#endif /* HAVE_DECNUMBER */