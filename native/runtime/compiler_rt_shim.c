/*
 * RazorForge Compiler-RT Shim
 *
 * Provides missing 128-bit integer runtime functions for Windows.
 * The runtime uses __int128 on 64-bit platforms (rf_divide.c), which requires these functions
 * that are normally provided by compiler-rt (Clang) or libgcc (GCC).
 *
 * On Windows, neither is typically available, so we provide our own.
 */

#include <stdint.h>

/*
 * Force these builtins into the DLL export table. CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS only exports symbols
 * from a target's OWN object files, NOT ones pulled in from a linked static library (this shim). Without an
 * explicit dllexport they stay linked-but-hidden, so the in-process ORC JIT — which resolves symbols via a
 * process-wide dynamic-library search over the loaded razorforge_runtime.dll's exports — can't find them, and
 * a JIT'd program doing 128-bit integer division/modulo fails with "Symbols not found: __udivti3 …". AOT
 * builds link compiler-rt directly and never hit this; only the JIT dev-loop needs the export.
 */
#if defined(_WIN32)
#define RT_EXPORT __declspec(dllexport)
#else
#define RT_EXPORT
#endif

/* Only needed when __int128 is available (64-bit platforms with Clang/GCC) */
#if defined(__SIZEOF_INT128__)

typedef unsigned __int128 tu_int;
typedef __int128 ti_int;

/* Helper: count leading zeros for 64-bit */
static inline int clz64_impl(uint64_t x) {
    if (x == 0) return 64;
#if defined(__GNUC__) || defined(__clang__)
    return __builtin_clzll(x);
#else
    int n = 0;
    if ((x & 0xFFFFFFFF00000000ULL) == 0) { n += 32; x <<= 32; }
    if ((x & 0xFFFF000000000000ULL) == 0) { n += 16; x <<= 16; }
    if ((x & 0xFF00000000000000ULL) == 0) { n += 8;  x <<= 8; }
    if ((x & 0xF000000000000000ULL) == 0) { n += 4;  x <<= 4; }
    if ((x & 0xC000000000000000ULL) == 0) { n += 2;  x <<= 2; }
    if ((x & 0x8000000000000000ULL) == 0) { n += 1; }
    return n;
#endif
}

/* Count leading zeros for 128-bit */
static inline int clz128(tu_int x) {
    uint64_t hi = (uint64_t)(x >> 64);
    uint64_t lo = (uint64_t)x;
    if (hi != 0)
        return clz64_impl(hi);
    return 64 + clz64_impl(lo);
}

/*
 * __udivti3 - Unsigned 128-bit division
 * Returns: a / b
 *
 * Algorithm: Binary long division
 */
RT_EXPORT tu_int __udivti3(tu_int a, tu_int b) {
    if (b == 0) {
        /* Division by zero - return max value (undefined behavior) */
        return ~(tu_int)0;
    }

    if (b > a) {
        return 0;
    }

    if (b == a) {
        return 1;
    }

    /* Fast path for divisor fits in 64 bits and dividend high is 0 */
    uint64_t b_hi = (uint64_t)(b >> 64);
    if (b_hi == 0) {
        uint64_t b_lo = (uint64_t)b;
        uint64_t a_hi = (uint64_t)(a >> 64);

        if (a_hi == 0) {
            /* Both fit in 64 bits */
            return (tu_int)((uint64_t)a / b_lo);
        }
    }

    /* Binary long division */
    int shift = clz128(b) - clz128(a);
    b <<= shift;

    tu_int quotient = 0;
    for (int i = 0; i <= shift; i++) {
        quotient <<= 1;
        if (a >= b) {
            a -= b;
            quotient |= 1;
        }
        b >>= 1;
    }

    return quotient;
}

/*
 * __umodti3 - Unsigned 128-bit modulo
 * Returns: a % b
 */
RT_EXPORT tu_int __umodti3(tu_int a, tu_int b) {
    if (b == 0) {
        return 0;  /* Undefined behavior */
    }

    if (b > a) {
        return a;
    }

    if (b == a) {
        return 0;
    }

    /* Fast path */
    uint64_t b_hi = (uint64_t)(b >> 64);
    if (b_hi == 0) {
        uint64_t b_lo = (uint64_t)b;
        uint64_t a_hi = (uint64_t)(a >> 64);

        if (a_hi == 0) {
            return (tu_int)((uint64_t)a % b_lo);
        }
    }

    /* Binary long division - we only need the remainder */
    int shift = clz128(b) - clz128(a);
    b <<= shift;

    for (int i = 0; i <= shift; i++) {
        if (a >= b) {
            a -= b;
        }
        b >>= 1;
    }

    return a;
}

/*
 * __divti3 - Signed 128-bit division
 * Returns: a / b
 */
RT_EXPORT ti_int __divti3(ti_int a, ti_int b) {
    int neg = 0;

    if (a < 0) {
        a = -a;
        neg = !neg;
    }
    if (b < 0) {
        b = -b;
        neg = !neg;
    }

    tu_int result = __udivti3((tu_int)a, (tu_int)b);

    if (neg) {
        return -(ti_int)result;
    }
    return (ti_int)result;
}

/*
 * __modti3 - Signed 128-bit modulo
 * Returns: a % b
 */
RT_EXPORT ti_int __modti3(ti_int a, ti_int b) {
    int neg = 0;

    if (a < 0) {
        a = -a;
        neg = 1;
    }
    if (b < 0) {
        b = -b;
    }

    tu_int result = __umodti3((tu_int)a, (tu_int)b);

    if (neg) {
        return -(ti_int)result;
    }
    return (ti_int)result;
}

/*
 * __udivmodti4 - Combined unsigned 128-bit division and modulo
 * Returns: a / b, stores a % b in *rem
 */
RT_EXPORT tu_int __udivmodti4(tu_int a, tu_int b, tu_int *rem) {
    if (b == 0) {
        if (rem) *rem = 0;
        return ~(tu_int)0;
    }

    if (b > a) {
        if (rem) *rem = a;
        return 0;
    }

    if (b == a) {
        if (rem) *rem = 0;
        return 1;
    }

    /* Fast path */
    uint64_t b_hi = (uint64_t)(b >> 64);
    if (b_hi == 0) {
        uint64_t b_lo = (uint64_t)b;
        uint64_t a_hi = (uint64_t)(a >> 64);

        if (a_hi == 0) {
            if (rem) *rem = (tu_int)((uint64_t)a % b_lo);
            return (tu_int)((uint64_t)a / b_lo);
        }
    }

    /* Binary long division */
    int shift = clz128(b) - clz128(a);
    b <<= shift;

    tu_int quotient = 0;
    for (int i = 0; i <= shift; i++) {
        quotient <<= 1;
        if (a >= b) {
            a -= b;
            quotient |= 1;
        }
        b >>= 1;
    }

    if (rem) *rem = a;
    return quotient;
}

#endif /* __SIZEOF_INT128__ */

/*
 * Half-precision conversions with the ABI LLVM expects on Windows x86-64.
 *
 * LLVM passes and returns `half` in XMM registers, but the clang_rt.builtins library shipped for
 * Windows was built with the old integer convention (the result in AX). With -mf16c float<->half
 * becomes an instruction, while double->half has none and stays a __truncdfhf2 libcall, which then
 * read garbage from XMM0 (B16(from: 0.15) came back as 0x3334). The runtime is linked before
 * compiler-rt, and the JIT resolves against this DLL's exports, so these correct definitions win in
 * both. Each rounds once to nearest, ties to even, and a NaN keeps its sign and top payload bits and
 * becomes quiet.
 */
#if defined(_WIN32) && defined(__clang__) && defined(__FLT16_MAX__)

#include <string.h>

static uint16_t rt_f64_to_f16_bits(uint64_t d) {
    uint16_t sign = (uint16_t)((d >> 48) & 0x8000u);
    uint64_t ab = d & 0x7FFFFFFFFFFFFFFFull;
    if (ab >= 0x7FF0000000000000ull) {
        if (ab > 0x7FF0000000000000ull)
            return (uint16_t)(sign | 0x7E00u | (uint16_t)((ab >> 42) & 0x3FFu));
        return (uint16_t)(sign | 0x7C00u);
    }
    int e = (int)(ab >> 52);
    uint64_t m = ab & 0xFFFFFFFFFFFFFull;
    int he = e - 1023 + 15;
    if (he >= 31)
        return (uint16_t)(sign | 0x7C00u);
    if (he <= 0) {
        /* binary16 subnormal (or zero): shift the full significand further right */
        if (e == 0)
            return sign;
        uint64_t sig = m | (1ull << 52);
        int shift = 43 - he;
        if (shift > 60)
            return sign;
        uint64_t q = sig >> shift;
        uint64_t rem = sig & ((1ull << shift) - 1);
        uint64_t half = 1ull << (shift - 1);
        if (rem > half || (rem == half && (q & 1)))
            q++;
        return (uint16_t)(sign | (uint16_t)q);
    }
    uint32_t h = ((uint32_t)he << 10) | (uint32_t)(m >> 42);
    uint64_t rem = m & ((1ull << 42) - 1);
    if (rem > (1ull << 41) || (rem == (1ull << 41) && (h & 1)))
        h++; /* a carry into the exponent is correct, up to +inf */
    return (uint16_t)(sign | h);
}

static _Float16 rt_f16_from_bits(uint16_t u) {
    _Float16 h;
    memcpy(&h, &u, sizeof h);
    return h;
}

RT_EXPORT _Float16 __truncdfhf2(double a) {
    uint64_t d;
    memcpy(&d, &a, sizeof d);
    return rt_f16_from_bits(rt_f64_to_f16_bits(d));
}

RT_EXPORT _Float16 __truncsfhf2(float a) {
    /* float -> double is exact, so this is still one rounding */
    double w = (double)a;
    uint64_t d;
    memcpy(&d, &w, sizeof d);
    return rt_f16_from_bits(rt_f64_to_f16_bits(d));
}

RT_EXPORT float __extendhfsf2(_Float16 a) {
    uint16_t u;
    memcpy(&u, &a, sizeof u);
    uint32_t sign = (uint32_t)(u & 0x8000u) << 16;
    uint32_t e = (u >> 10) & 0x1Fu;
    uint32_t m = u & 0x3FFu;
    uint32_t f;
    if (e == 0x1Fu) {
        f = sign | 0x7F800000u | (m << 13) | (m ? 0x00400000u : 0u);
    } else if (e != 0) {
        f = sign | ((e + 112u) << 23) | (m << 13);
    } else if (m == 0) {
        f = sign;
    } else {
        /* binary16 subnormal: normalize into a binary32 normal */
        int k = 0;
        while ((m & 0x400u) == 0) {
            m <<= 1;
            k++;
        }
        f = sign | ((uint32_t)(113 - k) << 23) | ((m & 0x3FFu) << 13);
    }
    float r;
    memcpy(&r, &f, sizeof r);
    return r;
}

#endif /* _WIN32 && __clang__ && __FLT16_MAX__ */
