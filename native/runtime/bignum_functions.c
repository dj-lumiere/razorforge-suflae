/*
 * RazorForge Runtime - Big Number Functions
 * Wrappers for LibTomMath (integers) and MAPM (decimals)
 */

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "../include/razorforge_math.h"

// ============================================================================
// LibTomMath wrappers for arbitrary precision integers
// ============================================================================

#ifdef HAVE_LIBTOMMATH
#include <tommath.h>

rf_bigint* rf_bigint_new(void)
{
    rf_bigint* a = (rf_bigint*)malloc(sizeof(rf_bigint));
    if (a)
    {
        mp_init((mp_int*)a);
    }
    return a;
}

int rf_bigint_init(rf_bigint* a)
{
    return mp_init((mp_int*)a);
}

void rf_bigint_clear(rf_bigint* a)
{
    if (a)
    {
        mp_clear((mp_int*)a);
        free(a);
    }
}

int rf_bigint_copy(rf_bigint* dest, rf_bigint* src)
{
    return mp_copy((mp_int*)src, (mp_int*)dest);
}

int rf_bigint_set_i64(rf_bigint* a, int64_t val)
{
    mp_set_i64((mp_int*)a, val);
    return 0;
}

int rf_bigint_set_u64(rf_bigint* a, uint64_t val)
{
    mp_set_u64((mp_int*)a, val);
    return 0;
}

int rf_bigint_set_str(rf_bigint* a, const char* str, int radix)
{
    return mp_read_radix((mp_int*)a, str, radix);
}

int64_t rf_bigint_get_i64(rf_bigint* a)
{
    return mp_get_i64((mp_int*)a);
}

uint64_t rf_bigint_get_u64(rf_bigint* a)
{
    return mp_get_u64((mp_int*)a);
}

char* rf_bigint_get_str(rf_bigint* a, int radix)
{
    size_t size;
    mp_radix_size((mp_int*)a, radix, &size);
    char* str = (char*)malloc(size);
    if (str)
    {
        mp_to_radix((mp_int*)a, str, size, NULL, radix);
    }
    return str;
}

int rf_bigint_add(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_add((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_sub(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_sub((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_mul(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_mul((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_div(rf_bigint* quotient, rf_bigint* remainder, rf_bigint* a, rf_bigint* b)
{
    return mp_div((mp_int*)a, (mp_int*)b, (mp_int*)quotient, (mp_int*)remainder);
}

int rf_bigint_mod(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_mod((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_neg(rf_bigint* result, rf_bigint* a)
{
    return mp_neg((mp_int*)a, (mp_int*)result);
}

int rf_bigint_abs(rf_bigint* result, rf_bigint* a)
{
    return mp_abs((mp_int*)a, (mp_int*)result);
}

int rf_bigint_cmp(rf_bigint* a, rf_bigint* b)
{
    return mp_cmp((mp_int*)a, (mp_int*)b);
}

int rf_bigint_cmp_i64(rf_bigint* a, int64_t b)
{
    mp_int tmp;
    mp_init(&tmp);
    mp_set_i64(&tmp, b);
    int result = mp_cmp((mp_int*)a, &tmp);
    mp_clear(&tmp);
    return result;
}

int rf_bigint_is_zero(rf_bigint* a)
{
    return mp_iszero((mp_int*)a);
}

int rf_bigint_is_neg(rf_bigint* a)
{
    return mp_isneg((mp_int*)a);
}

int rf_bigint_and(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_and((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_or(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_or((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_xor(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_xor((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_shl(rf_bigint* result, rf_bigint* a, int bits)
{
    return mp_mul_2d((mp_int*)a, bits, (mp_int*)result);
}

int rf_bigint_shr(rf_bigint* result, rf_bigint* a, int bits)
{
    return mp_div_2d((mp_int*)a, bits, (mp_int*)result, NULL);
}

int rf_bigint_pow(rf_bigint* result, rf_bigint* base, uint32_t exp)
{
    return mp_expt_n((mp_int*)base, (int)exp, (mp_int*)result);
}

int rf_bigint_sqrt(rf_bigint* result, rf_bigint* a)
{
    return mp_sqrt((mp_int*)a, (mp_int*)result);
}

int rf_bigint_gcd(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_gcd((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_lcm(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_lcm((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

#else
// Stub implementations when LibTomMath is not available

rf_bigint* rf_bigint_new(void)
{
    rf_bigint* a = (rf_bigint*)malloc(sizeof(rf_bigint));
    if (a)
    {
        memset(a, 0, sizeof(rf_bigint));
    }
    return a;
}

int rf_bigint_init(rf_bigint* a)
{
    memset(a, 0, sizeof(rf_bigint));
    return 0;
}

void rf_bigint_clear(rf_bigint* a)
{
    if (a)
    {
        if (a->dp) free(a->dp);
        free(a);
    }
}

int rf_bigint_copy(rf_bigint* dest, rf_bigint* src)
{
    if (!dest || !src) return -1;
    dest->used = src->used;
    dest->alloc = src->alloc;
    dest->sign = src->sign;
    if (src->dp)
    {
        dest->dp = malloc(sizeof(int64_t));
        if (dest->dp) *(int64_t*)dest->dp = *(int64_t*)src->dp;
    }
    return 0;
}

int rf_bigint_set_i64(rf_bigint* a, int64_t val)
{
    a->dp = malloc(sizeof(int64_t));
    if (a->dp) *(int64_t*)a->dp = val;
    a->used = 1;
    a->sign = val < 0 ? 1 : 0;
    return 0;
}

int rf_bigint_set_u64(rf_bigint* a, uint64_t val)
{
    a->dp = malloc(sizeof(uint64_t));
    if (a->dp) *(uint64_t*)a->dp = val;
    a->used = 1;
    a->sign = 0;
    return 0;
}

int rf_bigint_set_str(rf_bigint* a, const char* str, int radix)
{
    (void)radix;
    int64_t val = strtoll(str, NULL, radix);
    return rf_bigint_set_i64(a, val);
}

int64_t rf_bigint_get_i64(rf_bigint* a)
{
    if (a->dp) return *(int64_t*)a->dp;
    return 0;
}

uint64_t rf_bigint_get_u64(rf_bigint* a)
{
    if (a->dp) return *(uint64_t*)a->dp;
    return 0;
}

char* rf_bigint_get_str(rf_bigint* a, int radix)
{
    (void)radix;
    char* str = (char*)malloc(32);
    if (str) snprintf(str, 32, "%lld", (long long)rf_bigint_get_i64(a));
    return str;
}

// Stub arithmetic - uses int64_t (loses precision for large numbers)
int rf_bigint_add(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) + rf_bigint_get_i64(b));
}

int rf_bigint_sub(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) - rf_bigint_get_i64(b));
}

int rf_bigint_mul(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) * rf_bigint_get_i64(b));
}

int rf_bigint_div(rf_bigint* quotient, rf_bigint* remainder, rf_bigint* a, rf_bigint* b)
{
    int64_t av = rf_bigint_get_i64(a);
    int64_t bv = rf_bigint_get_i64(b);
    rf_bigint_set_i64(quotient, av / bv);
    rf_bigint_set_i64(remainder, av % bv);
    return 0;
}

int rf_bigint_mod(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) % rf_bigint_get_i64(b));
}

int rf_bigint_neg(rf_bigint* result, rf_bigint* a)
{
    return rf_bigint_set_i64(result, -rf_bigint_get_i64(a));
}

int rf_bigint_abs(rf_bigint* result, rf_bigint* a)
{
    int64_t v = rf_bigint_get_i64(a);
    return rf_bigint_set_i64(result, v < 0 ? -v : v);
}

int rf_bigint_cmp(rf_bigint* a, rf_bigint* b)
{
    int64_t av = rf_bigint_get_i64(a);
    int64_t bv = rf_bigint_get_i64(b);
    if (av < bv) return -1;
    if (av > bv) return 1;
    return 0;
}

int rf_bigint_cmp_i64(rf_bigint* a, int64_t b)
{
    int64_t av = rf_bigint_get_i64(a);
    if (av < b) return -1;
    if (av > b) return 1;
    return 0;
}

int rf_bigint_is_zero(rf_bigint* a)
{
    return rf_bigint_get_i64(a) == 0;
}

int rf_bigint_is_neg(rf_bigint* a)
{
    return rf_bigint_get_i64(a) < 0;
}

int rf_bigint_and(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) & rf_bigint_get_i64(b));
}

int rf_bigint_or(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) | rf_bigint_get_i64(b));
}

int rf_bigint_xor(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) ^ rf_bigint_get_i64(b));
}

int rf_bigint_shl(rf_bigint* result, rf_bigint* a, int bits)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) << bits);
}

int rf_bigint_shr(rf_bigint* result, rf_bigint* a, int bits)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) >> bits);
}

int rf_bigint_pow(rf_bigint* result, rf_bigint* base, uint32_t exp)
{
    int64_t b = rf_bigint_get_i64(base);
    int64_t r = 1;
    for (uint32_t i = 0; i < exp; i++) r *= b;
    return rf_bigint_set_i64(result, r);
}

int rf_bigint_sqrt(rf_bigint* result, rf_bigint* a)
{
    int64_t v = rf_bigint_get_i64(a);
    int64_t r = (int64_t)sqrt((double)v);
    return rf_bigint_set_i64(result, r);
}

int rf_bigint_gcd(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    int64_t av = rf_bigint_get_i64(a);
    int64_t bv = rf_bigint_get_i64(b);
    while (bv != 0)
    {
        int64_t t = bv;
        bv = av % bv;
        av = t;
    }
    return rf_bigint_set_i64(result, av < 0 ? -av : av);
}

int rf_bigint_lcm(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    rf_bigint gcd_result;
    rf_bigint_init(&gcd_result);
    rf_bigint_gcd(&gcd_result, a, b);
    int64_t av = rf_bigint_get_i64(a);
    int64_t bv = rf_bigint_get_i64(b);
    int64_t gv = rf_bigint_get_i64(&gcd_result);
    rf_bigint_clear(&gcd_result);
    return rf_bigint_set_i64(result, (av / gv) * bv);
}

#endif // HAVE_LIBTOMMATH

// ============================================================================
// rf_bigdec_* — arbitrary-precision decimal, decNumber-backed.
//
// Each rf_bigdecimal is a heap-allocated `decNumber` with capacity for
// DECNUMDIGITS digits (set below). Operations route through decNumber's
// arbitrary-precision API with a thread-shared default context.
//
// Coverage: lifecycle + set/get + comparison + arithmetic + sqrt + pow + exp
// + log/log10 + rounding (decNumber-native), plus trig / hyperbolic / pi / e
// routed through the vendored MIT LibBF binary core (see the LibBF-backed
// section below) — decNumber has no trig, and no permissively-licensed
// decimal-native transcendental library exists (MPFR/arb are LGPL).
// ============================================================================

// IMPORTANT: this file uses a much higher DECNUMDIGITS than decimal_functions.c
// (which uses 34 for D32/D64/D128). Don't pass `decNumber*` between the two
// translation units — the struct shape differs.
#undef DECNUMDIGITS
#define DECNUMDIGITS 1000

#ifdef HAVE_DECNUMBER
#include <decNumber.h>
#include <decContext.h>

#include <stdio.h>

// LibBF backs the trig/hyperbolic/pi section below (decNumber has no trig).
// libbf.h declares global mp_add/mp_sub/mp_mul limb helpers whose names
// collide with LibTomMath's public API (included above for Integer). The
// libbf target compiles with these renames (native/cmake/libbf.cmake), so
// mirror them here: the header's prototypes must land on the renamed symbols
// the DLL actually exports.
#define mp_add libbf_mp_add
#define mp_sub libbf_mp_sub
#define mp_mul libbf_mp_mul
#include "libbf.h"
#undef mp_add
#undef mp_sub
#undef mp_mul

static decContext* get_bigdec_ctx(void)
{
    static decContext ctx;
    static int inited = 0;
    if (!inited)
    {
        // DEC_INIT_DECIMAL128 starts with extended=1 + clamp=1, which is
        // required by decNumberLog/Log10/Exp/Power/SquareRoot/ToIntegralValue
        // (advanced math is gated on extended mode). DEC_INIT_BASE leaves
        // extended=0 and these functions silently no-op.
        decContextDefault(&ctx, DEC_INIT_DECIMAL128);
        ctx.digits = DECNUMDIGITS;
        // emax/emin must stay ≤ DEC_MAX_MATH (999999) — exceeding it makes
        // decNumberLog/Log10/Exp/Power/ToIntegralValue return NaN via
        // decCheckMath's DEC_Invalid_context flag. 999999 is the hard cap.
        ctx.emax = 999999;
        ctx.emin = -999999;
        ctx.round = DEC_ROUND_HALF_EVEN;
        ctx.traps = 0;
        ctx.clamp = 0;
        inited = 1;
    }
    return &ctx;
}

// -- precision control --------------------------------------------------------

// Updates the global working precision (digits) used by all rf_bigdec_* ops
// that don't take an explicit `precision` parameter. Clamped to [1, DECNUMDIGITS]
// and to DEC_MAX_MATH (decNumber's hard cap for math functions).
void rf_bigdec_set_precision(int digits)
{
    decContext* ctx = get_bigdec_ctx();
    if (digits < 1) digits = 1;
    if (digits > DECNUMDIGITS) digits = DECNUMDIGITS;
    if (digits > 999999) digits = 999999;  // DEC_MAX_MATH cap
    ctx->digits = digits;
}

// Reads the current global working precision.
int rf_bigdec_get_precision(void)
{
    return get_bigdec_ctx()->digits;
}

// -- lifecycle ----------------------------------------------------------------

rf_bigdecimal rf_bigdec_new(void)
{
    decNumber* n = (decNumber*)malloc(sizeof(decNumber));
    if (n) decNumberZero(n);
    return n;
}

void rf_bigdec_free(rf_bigdecimal a)
{
    free(a);
}

rf_bigdecimal rf_bigdec_copy(rf_bigdecimal a)
{
    decNumber* result = (decNumber*)malloc(sizeof(decNumber));
    if (result && a) decNumberCopy(result, (decNumber*)a);
    else if (result) decNumberZero(result);
    return result;
}

// -- set ----------------------------------------------------------------------

void rf_bigdec_set_s64(rf_bigdecimal a, int64_t val)
{
    if (!a) return;
    char buf[32];
    snprintf(buf, sizeof(buf), "%lld", (long long)val);
    decNumberFromString((decNumber*)a, buf, get_bigdec_ctx());
}

void rf_bigdec_set_f64(rf_bigdecimal a, double val)
{
    if (!a) return;
    // 17 digits is the round-trip precision for IEEE 754 binary64.
    char buf[64];
    snprintf(buf, sizeof(buf), "%.17g", val);
    decNumberFromString((decNumber*)a, buf, get_bigdec_ctx());
}

void rf_bigdec_set_str(rf_bigdecimal a, const char* str)
{
    if (!a || !str) return;
    decNumberFromString((decNumber*)a, str, get_bigdec_ctx());
}

// -- get ----------------------------------------------------------------------

// Rewrites decNumber's special spellings ("Infinity", "-Infinity", "NaN",
// "-NaN", "sNaN", ...) in place to the canonical RazorForge forms: "inf",
// "-inf", "NaN" (NaN is always unsigned). Finite values start with a digit,
// '-', '.', or '+' followed by digits, so the leading-letter checks cannot
// misfire on them.
static void canonicalize_bigdec_special(char* buf)
{
    int neg = buf[0] == '-';
    const char* p = buf + (neg ? 1 : 0);
    if (p[0] == 'I')                      // "Infinity"
        memcpy(buf, neg ? "-inf" : "inf\0", 5);
    else if (p[0] == 'N' || p[0] == 's')  // "NaN" / "sNaN"
        memcpy(buf, "NaN", 4);
}

char* rf_bigdec_get_str(rf_bigdecimal a, int decimal_places)
{
    if (!a) return NULL;
    // Caller frees. Allocate generously — decNumber's worst-case string length
    // is DECNUMDIGITS + 14 (sign, exponent, decimal point, sentinel).
    size_t cap = DECNUMDIGITS + 32;
    char* buf = (char*)malloc(cap);
    if (!buf) return NULL;

    if (decimal_places < 0)
    {
        // Free-form output — let decNumber pick exponent format.
        decNumberToString((decNumber*)a, buf);
    }
    else
    {
        // Rescale to the requested number of fractional digits before
        // printing. decNumberRescale requires a decNumber holding the target
        // exponent (which is -decimal_places).
        decNumber target;
        decNumber exp;
        decNumberZero(&exp);
        char expbuf[16];
        snprintf(expbuf, sizeof(expbuf), "%d", -decimal_places);
        decNumberFromString(&exp, expbuf, get_bigdec_ctx());
        decNumberRescale(&target, (decNumber*)a, &exp, get_bigdec_ctx());
        decNumberToString(&target, buf);
    }
    canonicalize_bigdec_special(buf);
    return buf;
}

int64_t rf_bigdec_get_s64(rf_bigdecimal a)
{
    if (!a) return 0;
    char* s = rf_bigdec_get_str(a, 0);
    if (!s) return 0;
    int64_t r = (int64_t)strtoll(s, NULL, 10);
    free(s);
    return r;
}

double rf_bigdec_get_f64(rf_bigdecimal a)
{
    if (!a) return 0.0;
    char* s = rf_bigdec_get_str(a, -1);
    if (!s) return 0.0;
    double r = strtod(s, NULL);
    free(s);
    return r;
}

// -- comparison ---------------------------------------------------------------

int rf_bigdec_cmp(rf_bigdecimal a, rf_bigdecimal b)
{
    if (!a || !b) return 0;
    decNumber r;
    decNumberCompare(&r, (decNumber*)a, (decNumber*)b, get_bigdec_ctx());
    if (decNumberIsZero(&r)) return 0;
    if (decNumberIsNegative(&r)) return -1;
    return 1;
}

int rf_bigdec_is_zero(rf_bigdecimal a)
{
    return a ? (decNumberIsZero((decNumber*)a) ? 1 : 0) : 1;
}

int rf_bigdec_is_neg(rf_bigdecimal a)
{
    return a ? (decNumberIsNegative((decNumber*)a) ? 1 : 0) : 0;
}

// -- arithmetic ---------------------------------------------------------------

void rf_bigdec_add(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b)
{
    if (!result || !a || !b) return;
    decNumberAdd((decNumber*)result, (decNumber*)a, (decNumber*)b, get_bigdec_ctx());
}

void rf_bigdec_sub(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b)
{
    if (!result || !a || !b) return;
    decNumberSubtract((decNumber*)result, (decNumber*)a, (decNumber*)b, get_bigdec_ctx());
}

void rf_bigdec_mul(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b)
{
    if (!result || !a || !b) return;
    decNumberMultiply((decNumber*)result, (decNumber*)a, (decNumber*)b, get_bigdec_ctx());
}

void rf_bigdec_div(rf_bigdecimal result, int precision, rf_bigdecimal a, rf_bigdecimal b)
{
    if (!result || !a || !b) return;
    decContext* ctx = get_bigdec_ctx();
    int saved = ctx->digits;
    if (precision > 0 && precision <= DECNUMDIGITS) ctx->digits = precision;
    decNumberDivide((decNumber*)result, (decNumber*)a, (decNumber*)b, ctx);
    ctx->digits = saved;
}

void rf_bigdec_neg(rf_bigdecimal result, rf_bigdecimal a)
{
    if (!result || !a) return;
    decNumberMinus((decNumber*)result, (decNumber*)a, get_bigdec_ctx());
}

void rf_bigdec_abs(rf_bigdecimal result, rf_bigdecimal a)
{
    if (!result || !a) return;
    decNumberAbs((decNumber*)result, (decNumber*)a, get_bigdec_ctx());
}

// -- math (sqrt/pow/exp/log/log10) -------------------------------------------

typedef decNumber* (*bigdec_unary_op)(decNumber*, const decNumber*, decContext*);

static void with_precision(int precision, bigdec_unary_op op,
                           decNumber* result, const decNumber* a)
{
    decContext* ctx = get_bigdec_ctx();
    int saved = ctx->digits;
    if (precision > 0 && precision <= DECNUMDIGITS) ctx->digits = precision;
    op(result, a, ctx);
    ctx->digits = saved;
}

void rf_bigdec_sqrt(rf_bigdecimal result, int precision, rf_bigdecimal a)
{
    if (!result || !a) return;
    with_precision(precision, decNumberSquareRoot, (decNumber*)result, (decNumber*)a);
}

void rf_bigdec_pow(rf_bigdecimal result, int precision, rf_bigdecimal base, rf_bigdecimal exp)
{
    if (!result || !base || !exp) return;
    decContext* ctx = get_bigdec_ctx();
    int saved = ctx->digits;
    if (precision > 0 && precision <= DECNUMDIGITS) ctx->digits = precision;
    decNumberPower((decNumber*)result, (decNumber*)base, (decNumber*)exp, ctx);
    ctx->digits = saved;
}

void rf_bigdec_exp(rf_bigdecimal result, int precision, rf_bigdecimal a)
{
    if (!result || !a) return;
    with_precision(precision, decNumberExp, (decNumber*)result, (decNumber*)a);
}

void rf_bigdec_log(rf_bigdecimal result, int precision, rf_bigdecimal a)
{
    if (!result || !a) return;
    with_precision(precision, decNumberLn, (decNumber*)result, (decNumber*)a);
}

void rf_bigdec_log10(rf_bigdecimal result, int precision, rf_bigdecimal a)
{
    if (!result || !a) return;
    with_precision(precision, decNumberLog10, (decNumber*)result, (decNumber*)a);
}

// -- trig / hyperbolic / constants (LibBF-backed) ------------------------------
//
// decNumber provides no trigonometric or hyperbolic functions, and the
// permissive-license arbitrary-precision landscape has no decimal-native
// transcendental engine (MPFR/arb are LGPL). These route through the vendored
// MIT LibBF binary core: decimal -> exact decimal string -> bf_t at
// (digits * log2(10) + 64 guard bits) -> correctly-rounded LibBF op ->
// (digits + 10)-digit string -> decimal rounded to the requested precision.
// The binary working precision scales WITH the request, so the final decimal
// rounding is the only rounding that matters — the same architecture every
// permissive implementation of this feature converges on (mpmath, big-math).
// Hyperbolics are composed from bf_exp with cancellation guards (LibBF has
// no sinh/cosh/tanh).

extern bf_context_t bf_ctx;
void ensure_bf_ctx(void);

// Resolves the same effective working precision rule as with_precision().
static int bigdec_effective_digits(int precision)
{
    decContext* ctx = get_bigdec_ctx();
    return (precision > 0 && precision <= DECNUMDIGITS) ? precision : ctx->digits;
}

static limb_t bigdec_digits_to_bits(int digits)
{
    return (limb_t)((double)digits * 3.3219280948873623) + 64;
}

static void bigdec_set_nan(decNumber* r)
{
    decNumberZero(r);
    r->bits = DECNAN;
}

// decimal -> binary. The decimal string is exact, so the only rounding is
// bf_atof's correct rounding to `prec` bits.
static void bigdec_to_bf(bf_t* out, const decNumber* a, limb_t prec)
{
    char buf[DECNUMDIGITS + 32];
    decNumberToString(a, buf);
    bf_atof(out, buf, NULL, 10, prec, BF_RNDN);
}

// binary -> decimal rounded to `digits`. Formats at digits + 10 significant
// digits so the decimal parse performs the only visible rounding. LibBF's
// "NaN"/"Inf" spellings parse cleanly as decNumber specials (and are
// re-canonicalized by rf_bigdec_get_str on output).
static void bf_to_bigdec(decNumber* result, const bf_t* v, int digits)
{
    size_t len;
    char* s = bf_ftoa(&len, v, 10, (limb_t)digits + 10,
                      BF_FTOA_FORMAT_FIXED | BF_RNDN);
    if (!s) { bigdec_set_nan(result); return; }
    decContext* ctx = get_bigdec_ctx();
    int saved = ctx->digits;
    ctx->digits = digits;
    decNumberFromString(result, s, ctx);
    // FIXED-format output keeps trailing zeros (e.g. a saturated tanh would
    // read back as 1.000…0); reduce to the canonical shortest coefficient,
    // matching Decimal.$represent's documented shape. Value is unchanged.
    decNumberReduce(result, result, ctx);
    ctx->digits = saved;
    bf_realloc(&bf_ctx, s, 0);
}

typedef int (*bigdec_bf_op)(bf_t*, const bf_t*, limb_t, bf_flags_t);

static void bigdec_bf_unary(decNumber* result, int precision, const decNumber* a,
                            bigdec_bf_op op)
{
    int digits = bigdec_effective_digits(precision);
    limb_t prec = bigdec_digits_to_bits(digits);
    ensure_bf_ctx();
    bf_t x, r;
    bf_init(&bf_ctx, &x);
    bf_init(&bf_ctx, &r);
    bigdec_to_bf(&x, a, prec);
    op(&r, &x, prec, BF_RNDN);
    bf_to_bigdec(result, &r, digits);
    bf_delete(&x);
    bf_delete(&r);
}

// sin/cos/tan/asin/acos: every special input (NaN, ±Infinity) yields NaN.
// asin/acos domain violations flow through LibBF, which returns NaN itself.
#define BIGDEC_TRIG(name, bf_fn)                                               \
    void rf_bigdec_##name(rf_bigdecimal result, int precision, rf_bigdecimal a) \
    {                                                                           \
        if (!result || !a) return;                                              \
        const decNumber* x = (const decNumber*)a;                               \
        if (decNumberIsSpecial(x)) { bigdec_set_nan((decNumber*)result); return; } \
        bigdec_bf_unary((decNumber*)result, precision, x, bf_fn);               \
    }

BIGDEC_TRIG(sin, bf_sin)
BIGDEC_TRIG(cos, bf_cos)
BIGDEC_TRIG(tan, bf_tan)
BIGDEC_TRIG(asin, bf_asin)
BIGDEC_TRIG(acos, bf_acos)

#undef BIGDEC_TRIG

void rf_bigdec_atan(rf_bigdecimal result, int precision, rf_bigdecimal a)
{
    if (!result || !a) return;
    decNumber* r = (decNumber*)result;
    const decNumber* x = (const decNumber*)a;
    if (decNumberIsNaN(x)) { bigdec_set_nan(r); return; }
    if (decNumberIsInfinite(x))
    {
        // atan(±inf) = ±pi/2 — the halving is exact in binary.
        int digits = bigdec_effective_digits(precision);
        ensure_bf_ctx();
        bf_t p;
        bf_init(&bf_ctx, &p);
        bf_const_pi(&p, bigdec_digits_to_bits(digits), BF_RNDN);
        bf_mul_2exp(&p, -1, BF_PREC_INF, BF_RNDN);
        bf_to_bigdec(r, &p, digits);
        bf_delete(&p);
        if (decNumberIsNegative(x)) decNumberCopyNegate(r, r);
        return;
    }
    bigdec_bf_unary(r, precision, x, bf_atan);
}

// Hyperbolics compose from bf_exp. For |x| < 1 the subtractions in sinh/tanh
// cancel ~|adjusted exponent| leading digits (sinh(x) ~ x), so the working
// precision grows by that many digits. Once x is so small that the cubic term
// falls below the result's ulp (adjexp < -(digits+4)/2), the correctly
// rounded result IS x rounded to the working precision.
static int bigdec_adjexp(const decNumber* x)
{
    return x->exponent + x->digits - 1;
}

void rf_bigdec_sinh(rf_bigdecimal result, int precision, rf_bigdecimal a)
{
    if (!result || !a) return;
    decNumber* r = (decNumber*)result;
    const decNumber* x = (const decNumber*)a;
    if (decNumberIsNaN(x)) { bigdec_set_nan(r); return; }
    if (decNumberIsInfinite(x) || decNumberIsZero(x)) { decNumberCopy(r, x); return; }

    int digits = bigdec_effective_digits(precision);
    int adjexp = bigdec_adjexp(x);
    if (adjexp < -(digits + 4) / 2 - 1)
    {
        with_precision(digits, decNumberPlus, r, x);
        return;
    }

    int guard = (adjexp < 0 ? -adjexp : 0) + 8;
    limb_t prec = bigdec_digits_to_bits(digits + guard);
    ensure_bf_ctx();
    bf_t bx, ex, exn;
    bf_init(&bf_ctx, &bx);
    bf_init(&bf_ctx, &ex);
    bf_init(&bf_ctx, &exn);
    bigdec_to_bf(&bx, x, prec);
    bf_exp(&ex, &bx, prec, BF_RNDN);
    bf_neg(&bx);
    bf_exp(&exn, &bx, prec, BF_RNDN);
    bf_sub(&ex, &ex, &exn, prec, BF_RNDN);
    bf_mul_2exp(&ex, -1, BF_PREC_INF, BF_RNDN);
    bf_to_bigdec(r, &ex, digits);
    bf_delete(&bx);
    bf_delete(&ex);
    bf_delete(&exn);
}

void rf_bigdec_cosh(rf_bigdecimal result, int precision, rf_bigdecimal a)
{
    if (!result || !a) return;
    decNumber* r = (decNumber*)result;
    const decNumber* x = (const decNumber*)a;
    if (decNumberIsNaN(x)) { bigdec_set_nan(r); return; }
    if (decNumberIsInfinite(x)) { decNumberCopyAbs(r, x); return; }

    int digits = bigdec_effective_digits(precision);
    limb_t prec = bigdec_digits_to_bits(digits + 8);  // sum never cancels
    ensure_bf_ctx();
    bf_t bx, ex, exn;
    bf_init(&bf_ctx, &bx);
    bf_init(&bf_ctx, &ex);
    bf_init(&bf_ctx, &exn);
    bigdec_to_bf(&bx, x, prec);
    bf_exp(&ex, &bx, prec, BF_RNDN);
    bf_neg(&bx);
    bf_exp(&exn, &bx, prec, BF_RNDN);
    bf_add(&ex, &ex, &exn, prec, BF_RNDN);
    bf_mul_2exp(&ex, -1, BF_PREC_INF, BF_RNDN);
    bf_to_bigdec(r, &ex, digits);
    bf_delete(&bx);
    bf_delete(&ex);
    bf_delete(&exn);
}

void rf_bigdec_tanh(rf_bigdecimal result, int precision, rf_bigdecimal a)
{
    if (!result || !a) return;
    decNumber* r = (decNumber*)result;
    const decNumber* x = (const decNumber*)a;
    if (decNumberIsNaN(x)) { bigdec_set_nan(r); return; }
    int digits = bigdec_effective_digits(precision);
    int large = decNumberIsInfinite(x);
    int adjexp = large ? 0 : bigdec_adjexp(x);
    // tanh saturates: |x| >= 10^7 puts 1 - |tanh x| below 10^-(8.6e6), far
    // beyond any representable working precision (digits <= 1000).
    if (large || adjexp >= 7)
    {
        decNumber one;
        decNumberFromInt32(&one, 1);
        decNumberCopySign(r, &one, x);
        return;
    }
    if (decNumberIsZero(x)) { decNumberCopy(r, x); return; }
    if (adjexp < -(digits + 4) / 2 - 1)
    {
        with_precision(digits, decNumberPlus, r, x);
        return;
    }

    // tanh(x) = (e^{2x} - 1) / (e^{2x} + 1)
    int guard = (adjexp < 0 ? -adjexp : 0) + 8;
    limb_t prec = bigdec_digits_to_bits(digits + guard);
    ensure_bf_ctx();
    bf_t bx, t, num, den, one;
    bf_init(&bf_ctx, &bx);
    bf_init(&bf_ctx, &t);
    bf_init(&bf_ctx, &num);
    bf_init(&bf_ctx, &den);
    bf_init(&bf_ctx, &one);
    bf_set_si(&one, 1);
    bigdec_to_bf(&bx, x, prec);
    bf_mul_2exp(&bx, 1, BF_PREC_INF, BF_RNDN);
    bf_exp(&t, &bx, prec, BF_RNDN);
    bf_sub(&num, &t, &one, prec, BF_RNDN);
    bf_add(&den, &t, &one, prec, BF_RNDN);
    bf_div(&t, &num, &den, prec, BF_RNDN);
    bf_to_bigdec(r, &t, digits);
    bf_delete(&bx);
    bf_delete(&t);
    bf_delete(&num);
    bf_delete(&den);
    bf_delete(&one);
}

void rf_bigdec_pi(rf_bigdecimal result, int precision)
{
    if (!result) return;
    int digits = bigdec_effective_digits(precision);
    ensure_bf_ctx();
    bf_t p;
    bf_init(&bf_ctx, &p);
    bf_const_pi(&p, bigdec_digits_to_bits(digits), BF_RNDN);
    bf_to_bigdec((decNumber*)result, &p, digits);
    bf_delete(&p);
}

void rf_bigdec_e(rf_bigdecimal result, int precision)
{
    if (!result) return;
    // decNumber's own exp is correctly rounded at context precision — no
    // binary detour needed for e = exp(1).
    decNumber one;
    decNumberFromInt32(&one, 1);
    with_precision(precision, decNumberExp, (decNumber*)result, &one);
}

// -- rounding -----------------------------------------------------------------

void rf_bigdec_ceil(rf_bigdecimal result, rf_bigdecimal a)
{
    if (!result || !a) return;
    decContext* ctx = get_bigdec_ctx();
    enum rounding saved = ctx->round;
    ctx->round = DEC_ROUND_CEILING;
    decNumberToIntegralValue((decNumber*)result, (decNumber*)a, ctx);
    ctx->round = saved;
}

void rf_bigdec_floor(rf_bigdecimal result, rf_bigdecimal a)
{
    if (!result || !a) return;
    decContext* ctx = get_bigdec_ctx();
    enum rounding saved = ctx->round;
    ctx->round = DEC_ROUND_FLOOR;
    decNumberToIntegralValue((decNumber*)result, (decNumber*)a, ctx);
    ctx->round = saved;
}

void rf_bigdec_round(rf_bigdecimal result, int decimal_places, rf_bigdecimal a)
{
    if (!result || !a) return;
    decContext* ctx = get_bigdec_ctx();
    decNumber target;
    char expbuf[16];
    snprintf(expbuf, sizeof(expbuf), "%d", -decimal_places);
    decNumberFromString(&target, expbuf, ctx);
    decNumberRescale((decNumber*)result, (decNumber*)a, &target, ctx);
}

void rf_bigdec_trunc(rf_bigdecimal result, int decimal_places, rf_bigdecimal a)
{
    if (!result || !a) return;
    decContext* ctx = get_bigdec_ctx();
    enum rounding saved = ctx->round;
    ctx->round = DEC_ROUND_DOWN;
    decNumber target;
    char expbuf[16];
    snprintf(expbuf, sizeof(expbuf), "%d", -decimal_places);
    decNumberFromString(&target, expbuf, ctx);
    decNumberRescale((decNumber*)result, (decNumber*)a, &target, ctx);
    ctx->round = saved;
}

#endif // HAVE_DECNUMBER