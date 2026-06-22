/*
 * RazorForge Runtime - Real (arbitrary-precision binary float) via libbf.
 *
 * `Real` is the arbitrary-precision real type, a value record mirroring `Integer`
 * (a refcounted CPtr handle). Each handle owns a heap `bf_t`. add/sub/mul are exact
 * (BF_PREC_INF); div/sqrt/transcendentals round to a generous default working
 * precision (RF_REAL_PREC bits) under round-to-nearest-ties-to-even.
 */
#include <stdlib.h>
#include <stdint.h>
#include "libbf.h"

/* Default working precision (bits) for inexact operations (div / sqrt / libm).
 * 1024 bits ~ 308 decimal digits. */
#define RF_REAL_PREC ((limb_t)1024)
#define RF_REAL_RND  BF_RNDN

static bf_context_t rf_real_ctx;
static int rf_real_ctx_inited = 0;

static void *rf_real_realloc(void *opaque, void *ptr, size_t size) {
    (void)opaque;
    if (size == 0) { free(ptr); return NULL; }
    return realloc(ptr, size);
}

static void rf_real_ensure_ctx(void) {
    if (!rf_real_ctx_inited) {
        bf_context_init(&rf_real_ctx, rf_real_realloc, NULL);
        rf_real_ctx_inited = 1;
    }
}

/* --- lifecycle (mirrors rf_bigint_new / rf_bigint_clear) --- */
void *rf_real_new(void) {
    rf_real_ensure_ctx();
    bf_t *r = (bf_t *)malloc(sizeof(bf_t));
    bf_init(&rf_real_ctx, r);
    return r;
}
void rf_real_clear(void *handle) {
    if (!handle) return;
    bf_delete((bf_t *)handle);
    free(handle);
}

/* --- setters / getters --- */
void rf_real_set_i64(void *handle, int64_t value) { bf_set_si((bf_t *)handle, value); }
void rf_real_set_f64(void *handle, double value)   { bf_set_float64((bf_t *)handle, value); }
int  rf_real_set_str(void *handle, const char *str, int radix) {
    return bf_atof((bf_t *)handle, str, NULL, radix, RF_REAL_PREC, RF_REAL_RND);
}
double rf_real_get_f64(void *handle) {
    double d;
    bf_get_float64((bf_t *)handle, &d, BF_RNDN);
    return d;
}
/* Caller must free the returned string with rf_real_free_str. */
char *rf_real_to_str(void *handle) {
    /* FORMAT_FREE at radix 10 renders the value rounded to `prec` BITS using the
     * fewest digits needed; pass the working precision so the full value prints. */
    return bf_ftoa(NULL, (bf_t *)handle, 10, RF_REAL_PREC, BF_FTOA_FORMAT_FREE_MIN | BF_RNDN);
}
void rf_real_free_str(char *s) { free(s); }

/* --- arithmetic: + - * exact (BF_PREC_INF); / rounded --- */
void rf_real_add(void *r, void *a, void *b) { bf_add((bf_t *)r, (bf_t *)a, (bf_t *)b, BF_PREC_INF, BF_RNDN); }
void rf_real_sub(void *r, void *a, void *b) { bf_sub((bf_t *)r, (bf_t *)a, (bf_t *)b, BF_PREC_INF, BF_RNDN); }
void rf_real_mul(void *r, void *a, void *b) { bf_mul((bf_t *)r, (bf_t *)a, (bf_t *)b, BF_PREC_INF, BF_RNDN); }
void rf_real_div(void *r, void *a, void *b) { bf_div((bf_t *)r, (bf_t *)a, (bf_t *)b, RF_REAL_PREC, BF_RNDN); }
void rf_real_neg(void *r, void *a) { bf_set((bf_t *)r, (bf_t *)a); bf_neg((bf_t *)r); }

/* --- libm surface (rounded to RF_REAL_PREC) --- */
void rf_real_sqrt(void *r, void *a)         { bf_sqrt((bf_t *)r, (bf_t *)a, RF_REAL_PREC, BF_RNDN); }
void rf_real_exp(void *r, void *a)          { bf_exp((bf_t *)r, (bf_t *)a, RF_REAL_PREC, BF_RNDN); }
void rf_real_log(void *r, void *a)          { bf_log((bf_t *)r, (bf_t *)a, RF_REAL_PREC, BF_RNDN); }
void rf_real_pow(void *r, void *a, void *b) { bf_pow((bf_t *)r, (bf_t *)a, (bf_t *)b, RF_REAL_PREC, BF_RNDN); }
void rf_real_sin(void *r, void *a)          { bf_sin((bf_t *)r, (bf_t *)a, RF_REAL_PREC, BF_RNDN); }
void rf_real_cos(void *r, void *a)          { bf_cos((bf_t *)r, (bf_t *)a, RF_REAL_PREC, BF_RNDN); }

/* --- round-to-integral (bf_rint rounds in place; copy then round) --- */
void rf_real_floor(void *r, void *a) { bf_set((bf_t *)r, (bf_t *)a); bf_rint((bf_t *)r, BF_RNDD);  }
void rf_real_ceil(void *r, void *a)  { bf_set((bf_t *)r, (bf_t *)a); bf_rint((bf_t *)r, BF_RNDU);  }
void rf_real_trunc(void *r, void *a) { bf_set((bf_t *)r, (bf_t *)a); bf_rint((bf_t *)r, BF_RNDZ);  }
void rf_real_round(void *r, void *a) { bf_set((bf_t *)r, (bf_t *)a); bf_rint((bf_t *)r, BF_RNDNA); }
void rf_real_rint(void *r, void *a)  { bf_set((bf_t *)r, (bf_t *)a); bf_rint((bf_t *)r, BF_RNDN);  }

/* ------------------------------------------------------------------------- *
 * Extended transcendental surface (precision-aware).
 *
 * `_p` variants take the requested DECIMAL precision (significant digits) and
 * round to a matching binary working precision (digits * log2(10) + guard).
 * The plain variants use the default RF_REAL_PREC. Functions libbf lacks
 * (hyperbolics, log2/log10, hypot, cbrt) are composed from exp/log/sqrt/pow.
 * Temporaries are allocated in rf_real_ctx and freed before return.
 * ------------------------------------------------------------------------- */

/* Decimal significant digits -> binary working precision in bits (+16 guard).
 * <=0 digits falls back to the default precision. 3402/1024 ~= log2(10). */
static limb_t rf_prec_bits(int32_t digits) {
    if (digits <= 0) return RF_REAL_PREC;
    return ((limb_t)digits * 3402) / 1024 + 16;
}

/* --- direct libbf wraps (default + precision-aware) --- */
void rf_real_tan(void *r, void *a)   { bf_tan((bf_t *)r, (bf_t *)a, RF_REAL_PREC, BF_RNDN); }
void rf_real_asin(void *r, void *a)  { bf_asin((bf_t *)r, (bf_t *)a, RF_REAL_PREC, BF_RNDN); }
void rf_real_acos(void *r, void *a)  { bf_acos((bf_t *)r, (bf_t *)a, RF_REAL_PREC, BF_RNDN); }
void rf_real_atan(void *r, void *a)  { bf_atan((bf_t *)r, (bf_t *)a, RF_REAL_PREC, BF_RNDN); }
void rf_real_atan2(void *r, void *y, void *x) { bf_atan2((bf_t *)r, (bf_t *)y, (bf_t *)x, RF_REAL_PREC, BF_RNDN); }

void rf_real_div_p(void *r, void *a, void *b, int32_t d)  { bf_div((bf_t *)r, (bf_t *)a, (bf_t *)b, rf_prec_bits(d), BF_RNDN); }
void rf_real_sqrt_p(void *r, void *a, int32_t d)          { bf_sqrt((bf_t *)r, (bf_t *)a, rf_prec_bits(d), BF_RNDN); }
void rf_real_exp_p(void *r, void *a, int32_t d)           { bf_exp((bf_t *)r, (bf_t *)a, rf_prec_bits(d), BF_RNDN); }
void rf_real_log_p(void *r, void *a, int32_t d)           { bf_log((bf_t *)r, (bf_t *)a, rf_prec_bits(d), BF_RNDN); }
void rf_real_pow_p(void *r, void *a, void *b, int32_t d)  { bf_pow((bf_t *)r, (bf_t *)a, (bf_t *)b, rf_prec_bits(d), BF_RNDN); }
void rf_real_sin_p(void *r, void *a, int32_t d)           { bf_sin((bf_t *)r, (bf_t *)a, rf_prec_bits(d), BF_RNDN); }
void rf_real_cos_p(void *r, void *a, int32_t d)           { bf_cos((bf_t *)r, (bf_t *)a, rf_prec_bits(d), BF_RNDN); }
void rf_real_tan_p(void *r, void *a, int32_t d)           { bf_tan((bf_t *)r, (bf_t *)a, rf_prec_bits(d), BF_RNDN); }
void rf_real_asin_p(void *r, void *a, int32_t d)          { bf_asin((bf_t *)r, (bf_t *)a, rf_prec_bits(d), BF_RNDN); }
void rf_real_acos_p(void *r, void *a, int32_t d)          { bf_acos((bf_t *)r, (bf_t *)a, rf_prec_bits(d), BF_RNDN); }
void rf_real_atan_p(void *r, void *a, int32_t d)          { bf_atan((bf_t *)r, (bf_t *)a, rf_prec_bits(d), BF_RNDN); }
void rf_real_atan2_p(void *r, void *y, void *x, int32_t d){ bf_atan2((bf_t *)r, (bf_t *)y, (bf_t *)x, rf_prec_bits(d), BF_RNDN); }

/* --- composed: hyperbolics --- */
/* sinh(x) = (e^x - e^-x)/2 ; cosh(x) = (e^x + e^-x)/2 */
static void rl_sinhcosh(bf_t *r, const bf_t *a, limb_t prec, int is_cosh) {
    rf_real_ensure_ctx();
    bf_t ex, enx, na;
    bf_init(&rf_real_ctx, &ex); bf_init(&rf_real_ctx, &enx); bf_init(&rf_real_ctx, &na);
    bf_exp(&ex, a, prec, BF_RNDN);
    bf_set(&na, a); bf_neg(&na);
    bf_exp(&enx, &na, prec, BF_RNDN);
    if (is_cosh) bf_add(r, &ex, &enx, prec, BF_RNDN);
    else         bf_sub(r, &ex, &enx, prec, BF_RNDN);
    bf_mul_2exp(r, -1, prec, BF_RNDN); /* / 2 */
    bf_delete(&ex); bf_delete(&enx); bf_delete(&na);
}
/* tanh(x) = (e^2x - 1)/(e^2x + 1) */
static void rl_tanh(bf_t *r, const bf_t *a, limb_t prec) {
    rf_real_ensure_ctx();
    bf_t e2, num, den, one;
    bf_init(&rf_real_ctx, &e2); bf_init(&rf_real_ctx, &num);
    bf_init(&rf_real_ctx, &den); bf_init(&rf_real_ctx, &one);
    bf_set(&e2, a); bf_mul_2exp(&e2, 1, prec, BF_RNDN); /* 2x */
    bf_exp(&e2, &e2, prec, BF_RNDN);                    /* e^2x */
    bf_set_si(&one, 1);
    bf_sub(&num, &e2, &one, prec, BF_RNDN);
    bf_add(&den, &e2, &one, prec, BF_RNDN);
    bf_div(r, &num, &den, prec, BF_RNDN);
    bf_delete(&e2); bf_delete(&num); bf_delete(&den); bf_delete(&one);
}
/* asinh(x) = log(x + sqrt(x^2 + 1)) ; acosh(x) = log(x + sqrt(x^2 - 1)) */
static void rl_asinh_acosh(bf_t *r, const bf_t *a, limb_t prec, int is_acosh) {
    rf_real_ensure_ctx();
    bf_t t, one;
    bf_init(&rf_real_ctx, &t); bf_init(&rf_real_ctx, &one);
    bf_mul(&t, a, a, prec, BF_RNDN);   /* x^2 */
    bf_set_si(&one, 1);
    if (is_acosh) bf_sub(&t, &t, &one, prec, BF_RNDN);
    else          bf_add(&t, &t, &one, prec, BF_RNDN);
    bf_sqrt(&t, &t, prec, BF_RNDN);
    bf_add(&t, &t, a, prec, BF_RNDN);  /* x + sqrt(...) */
    bf_log(r, &t, prec, BF_RNDN);
    bf_delete(&t); bf_delete(&one);
}
/* atanh(x) = 0.5 * log((1 + x)/(1 - x)) */
static void rl_atanh(bf_t *r, const bf_t *a, limb_t prec) {
    rf_real_ensure_ctx();
    bf_t num, den, one;
    bf_init(&rf_real_ctx, &num); bf_init(&rf_real_ctx, &den); bf_init(&rf_real_ctx, &one);
    bf_set_si(&one, 1);
    bf_add(&num, &one, a, prec, BF_RNDN);
    bf_sub(&den, &one, a, prec, BF_RNDN);
    bf_div(&num, &num, &den, prec, BF_RNDN);
    bf_log(r, &num, prec, BF_RNDN);
    bf_mul_2exp(r, -1, prec, BF_RNDN); /* * 0.5 */
    bf_delete(&num); bf_delete(&den); bf_delete(&one);
}
/* log2(x) = log(x)/ln(2) ; log10(x) = log(x)/log(10) */
static void rl_logbase(bf_t *r, const bf_t *a, limb_t prec, int base10) {
    rf_real_ensure_ctx();
    bf_t l, lb;
    bf_init(&rf_real_ctx, &l); bf_init(&rf_real_ctx, &lb);
    bf_log(&l, a, prec, BF_RNDN);
    if (base10) { bf_set_si(&lb, 10); bf_log(&lb, &lb, prec, BF_RNDN); }
    else        { bf_const_log2(&lb, prec, BF_RNDN); }
    bf_div(r, &l, &lb, prec, BF_RNDN);
    bf_delete(&l); bf_delete(&lb);
}
/* hypot(a,b) = sqrt(a^2 + b^2) */
static void rl_hypot(bf_t *r, const bf_t *a, const bf_t *b, limb_t prec) {
    rf_real_ensure_ctx();
    bf_t a2, b2;
    bf_init(&rf_real_ctx, &a2); bf_init(&rf_real_ctx, &b2);
    bf_mul(&a2, a, a, prec, BF_RNDN);
    bf_mul(&b2, b, b, prec, BF_RNDN);
    bf_add(&a2, &a2, &b2, prec, BF_RNDN);
    bf_sqrt(r, &a2, prec, BF_RNDN);
    bf_delete(&a2); bf_delete(&b2);
}
/* cbrt(x) = sign(x) * pow(|x|, 1/3) (pow rejects negative bases) */
static void rl_cbrt(bf_t *r, const bf_t *a, limb_t prec) {
    rf_real_ensure_ctx();
    bf_t ax, third, three, one, zero;
    bf_init(&rf_real_ctx, &ax); bf_init(&rf_real_ctx, &third);
    bf_init(&rf_real_ctx, &three); bf_init(&rf_real_ctx, &one); bf_init(&rf_real_ctx, &zero);
    bf_set_si(&zero, 0);
    int neg = bf_cmp_lt(a, &zero);
    bf_set(&ax, a); if (neg) bf_neg(&ax);
    bf_set_si(&one, 1); bf_set_si(&three, 3);
    bf_div(&third, &one, &three, prec, BF_RNDN);
    bf_pow(r, &ax, &third, prec, BF_RNDN);
    if (neg) bf_neg(r);
    bf_delete(&ax); bf_delete(&third); bf_delete(&three); bf_delete(&one); bf_delete(&zero);
}

void rf_real_sinh(void *r, void *a)  { rl_sinhcosh((bf_t *)r, (bf_t *)a, RF_REAL_PREC, 0); }
void rf_real_cosh(void *r, void *a)  { rl_sinhcosh((bf_t *)r, (bf_t *)a, RF_REAL_PREC, 1); }
void rf_real_tanh(void *r, void *a)  { rl_tanh((bf_t *)r, (bf_t *)a, RF_REAL_PREC); }
void rf_real_asinh(void *r, void *a) { rl_asinh_acosh((bf_t *)r, (bf_t *)a, RF_REAL_PREC, 0); }
void rf_real_acosh(void *r, void *a) { rl_asinh_acosh((bf_t *)r, (bf_t *)a, RF_REAL_PREC, 1); }
void rf_real_atanh(void *r, void *a) { rl_atanh((bf_t *)r, (bf_t *)a, RF_REAL_PREC); }
void rf_real_log2(void *r, void *a)  { rl_logbase((bf_t *)r, (bf_t *)a, RF_REAL_PREC, 0); }
void rf_real_log10(void *r, void *a) { rl_logbase((bf_t *)r, (bf_t *)a, RF_REAL_PREC, 1); }
void rf_real_hypot(void *r, void *a, void *b) { rl_hypot((bf_t *)r, (bf_t *)a, (bf_t *)b, RF_REAL_PREC); }
void rf_real_cbrt(void *r, void *a)  { rl_cbrt((bf_t *)r, (bf_t *)a, RF_REAL_PREC); }

void rf_real_sinh_p(void *r, void *a, int32_t d)  { rl_sinhcosh((bf_t *)r, (bf_t *)a, rf_prec_bits(d), 0); }
void rf_real_cosh_p(void *r, void *a, int32_t d)  { rl_sinhcosh((bf_t *)r, (bf_t *)a, rf_prec_bits(d), 1); }
void rf_real_tanh_p(void *r, void *a, int32_t d)  { rl_tanh((bf_t *)r, (bf_t *)a, rf_prec_bits(d)); }
void rf_real_asinh_p(void *r, void *a, int32_t d) { rl_asinh_acosh((bf_t *)r, (bf_t *)a, rf_prec_bits(d), 0); }
void rf_real_acosh_p(void *r, void *a, int32_t d) { rl_asinh_acosh((bf_t *)r, (bf_t *)a, rf_prec_bits(d), 1); }
void rf_real_atanh_p(void *r, void *a, int32_t d) { rl_atanh((bf_t *)r, (bf_t *)a, rf_prec_bits(d)); }
void rf_real_log2_p(void *r, void *a, int32_t d)  { rl_logbase((bf_t *)r, (bf_t *)a, rf_prec_bits(d), 0); }
void rf_real_log10_p(void *r, void *a, int32_t d) { rl_logbase((bf_t *)r, (bf_t *)a, rf_prec_bits(d), 1); }
void rf_real_hypot_p(void *r, void *a, void *b, int32_t d) { rl_hypot((bf_t *)r, (bf_t *)a, (bf_t *)b, rf_prec_bits(d)); }
void rf_real_cbrt_p(void *r, void *a, int32_t d)  { rl_cbrt((bf_t *)r, (bf_t *)a, rf_prec_bits(d)); }

/* --- comparison (mirrors rf_bigint_cmp): -1 / 0 / 1, or 2 for unordered (NaN) --- */
int32_t rf_real_cmp(void *a, void *b) { return (int32_t)bf_cmp((bf_t *)a, (bf_t *)b); }
