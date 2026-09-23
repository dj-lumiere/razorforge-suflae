/*
 * RazorForge Runtime - Word-division primitives
 *
 * A fully self-contained translation unit (only <stdint.h> / <intrin.h>, no razorforge_math.h).
 * That lets the dev-loop LTO step compile it straight to LLVM bitcode and llvm-link it into the
 * RF module before opt, so the reciprocal/divide shims inline at the RF<->runtime seam
 * (rf_reciprocal_word folds to a constant 0 on x86-64; rf_udivrem_128_64_pre inlines
 * its `divq`). See src/BuildSystem/NativeToolchain.cs (BuildHotRuntimeBitcode).
 */

#include <stdint.h>
#if defined(_MSC_VER)
#include <intrin.h>
#endif

// Force LLVM to inline these across the RF<->runtime seam under dev-loop LTO (llvm-link + opt):
// the divq/reciprocal shims are tiny and hot, and the default inliner leaves them out-of-line
// (measured: a cross-DLL divq call halved when merely brought in-module, and inlining it into the
// caller loop closes the rest of the gap). The functions stay EXTERNAL — still emitted and exported
// from razorforge_runtime for the non-LTO FFI path — the attribute only adds `alwaysinline`, which
// llvm-link carries into the RF module so opt's AlwaysInliner folds each into its call sites.
#define RF_ALWAYS_INLINE __attribute__((always_inline))

// ============================================================================
// TEMPORARY (bench-only): hardware 128/64 -> 64 unsigned divide, the old M-G-replaced
// primitive. Kept solely so div_ab_bench.rf can A/B the shim's `divq` against M-G.
// Remove once benchmarking is done.
// ============================================================================
RF_ALWAYS_INLINE uint64_t rf_udivrem_128_64(uint64_t hi, uint64_t lo, uint64_t d, uint64_t* rem) {
#if defined(__x86_64__) && (defined(__GNUC__) || defined(__clang__))
    uint64_t q, r;
    __asm__("divq %[d]" : "=a"(q), "=d"(r) : "a"(lo), "d"(hi), [d] "r"(d));
    *rem = r;
    return q;
#elif defined(_MSC_VER) && defined(_M_X64)
    return _udiv128(hi, lo, d, rem);
#else
    unsigned __int128 n = ((unsigned __int128)hi << 64) | (unsigned __int128)lo;
    *rem = (uint64_t)(n % d);
    return (uint64_t)(n / d);
#endif
}

// ============================================================================
// Möller-Granlund reciprocal division for targets WITHOUT a hardware 128/64
// divide (arm64, wasm, ...). On x86-64 the `_pre` primitive uses `divq`/`_udiv128`
// and the reciprocal is a no-op; only the #else paths compile/run the M-G code.
// ============================================================================
#if (defined(__x86_64__) && (defined(__GNUC__) || defined(__clang__))) || (defined(_MSC_VER) && defined(_M_X64))
#define RF_HAS_HW_UDIV128 1
#else
#define RF_HAS_HW_UDIV128 0
#endif

#if !RF_HAS_HW_UDIV128
// reciprocal_2by1 seed table (intx): t[i] = 0x7fd00 / (i + 256).
static const uint16_t mg_recip_seed[256] = {
  2045, 2037, 2029, 2021, 2013, 2005, 1998, 1990, 1983, 1975, 1968, 1960, 1953, 1946, 1938, 1931,
  1924, 1917, 1910, 1903, 1896, 1889, 1883, 1876, 1869, 1863, 1856, 1849, 1843, 1836, 1830, 1824,
  1817, 1811, 1805, 1799, 1792, 1786, 1780, 1774, 1768, 1762, 1756, 1750, 1745, 1739, 1733, 1727,
  1722, 1716, 1710, 1705, 1699, 1694, 1688, 1683, 1677, 1672, 1667, 1661, 1656, 1651, 1646, 1641,
  1636, 1630, 1625, 1620, 1615, 1610, 1605, 1600, 1596, 1591, 1586, 1581, 1576, 1572, 1567, 1562,
  1558, 1553, 1548, 1544, 1539, 1535, 1530, 1526, 1521, 1517, 1513, 1508, 1504, 1500, 1495, 1491,
  1487, 1483, 1478, 1474, 1470, 1466, 1462, 1458, 1454, 1450, 1446, 1442, 1438, 1434, 1430, 1426,
  1422, 1418, 1414, 1411, 1407, 1403, 1399, 1396, 1392, 1388, 1384, 1381, 1377, 1374, 1370, 1366,
  1363, 1359, 1356, 1352, 1349, 1345, 1342, 1338, 1335, 1332, 1328, 1325, 1322, 1318, 1315, 1312,
  1308, 1305, 1302, 1299, 1295, 1292, 1289, 1286, 1283, 1280, 1276, 1273, 1270, 1267, 1264, 1261,
  1258, 1255, 1252, 1249, 1246, 1243, 1240, 1237, 1234, 1231, 1228, 1226, 1223, 1220, 1217, 1214,
  1211, 1209, 1206, 1203, 1200, 1197, 1195, 1192, 1189, 1187, 1184, 1181, 1179, 1176, 1173, 1171,
  1168, 1165, 1163, 1160, 1158, 1155, 1153, 1150, 1148, 1145, 1143, 1140, 1138, 1135, 1133, 1130,
  1128, 1125, 1123, 1121, 1118, 1116, 1113, 1111, 1109, 1106, 1104, 1102, 1099, 1097, 1095, 1092,
  1090, 1088, 1086, 1083, 1081, 1079, 1077, 1074, 1072, 1070, 1068, 1066, 1064, 1061, 1059, 1057,
  1055, 1053, 1051, 1049, 1047, 1044, 1042, 1040, 1038, 1036, 1034, 1032, 1030, 1028, 1026, 1024
};
// Moller-Granlund Algorithm 3: reciprocal of a NORMALIZED d (MSB set). Divide-free
// (table seed + multiply-only Newton); intx reciprocal_2by1 verbatim.
static uint64_t mg_reciprocal_2by1(uint64_t d) {
    uint64_t d9  = d >> 55;
    uint64_t v0  = mg_recip_seed[d9 - 256];
    uint64_t d40 = (d >> 24) + 1;
    uint64_t v1  = (v0 << 11) - ((v0 * v0 * d40) >> 40) - 1;
    uint64_t v2  = (v1 << 13) + ((v1 * (0x1000000000000000ULL - v1 * d40)) >> 47);
    uint64_t d0  = d & 1;
    uint64_t d63 = (d >> 1) + d0;
    uint64_t e   = ((v2 >> 1) & (0 - d0)) - v2 * d63;
    uint64_t v3  = (uint64_t)(((unsigned __int128)v2 * e) >> 64);
    v3 = (v3 >> 1) + (v2 << 31);
    uint64_t v4  = v3 - (uint64_t)(((unsigned __int128)v3 * d + d) >> 64) - d;
    return v4;
}
#endif

RF_ALWAYS_INLINE uint64_t rf_reciprocal_word(uint64_t d) {
#if RF_HAS_HW_UDIV128
    (void)d;
    return 0;
#else
    return mg_reciprocal_2by1(d);
#endif
}

RF_ALWAYS_INLINE uint64_t rf_udivrem_128_64_pre(uint64_t hi, uint64_t lo, uint64_t d, uint64_t v, uint64_t* rem) {
#if defined(__x86_64__) && (defined(__GNUC__) || defined(__clang__))
    (void)v;
    uint64_t q, r;
    __asm__("divq %[d]" : "=a"(q), "=d"(r) : "a"(lo), "d"(hi), [d] "r"(d));
    *rem = r;
    return q;
#elif defined(_MSC_VER) && defined(_M_X64)
    (void)v;
    return _udiv128(hi, lo, d, rem);
#else
    unsigned __int128 q = (unsigned __int128)v * (unsigned __int128)hi;
    q += ((unsigned __int128)hi << 64) | (unsigned __int128)lo;
    uint64_t q1 = (uint64_t)(q >> 64) + 1;
    uint64_t q0 = (uint64_t)q;
    uint64_t r  = lo - q1 * d;
    if (r > q0) { q1--; r += d; }
    if (r >= d) { q1++; r -= d; }
    *rem = r;
    return q1;
#endif
}
