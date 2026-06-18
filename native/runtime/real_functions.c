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

/* --- comparison (mirrors rf_bigint_cmp): -1 / 0 / 1, or 2 for unordered (NaN) --- */
int32_t rf_real_cmp(void *a, void *b) { return (int32_t)bf_cmp((bf_t *)a, (bf_t *)b); }
