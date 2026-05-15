using System.Runtime.InteropServices;

namespace RazorForge.Tests.Meta;

/// <summary>
/// Smoke tests for the <c>rf_bigdec_*</c> arbitrary-precision decimal runtime
/// surface exposed by <c>razorforge_runtime</c>. Verifies the symbols actually
/// link (catches the kind of regression that hit CI when decNumber wasn't
/// cloned) and that the core decNumber-backed operations produce correct
/// results.
///
/// Coverage today: lifecycle, set/get (s64/f64/str), comparison, arithmetic
/// (add/sub/mul/div/neg/abs), math (sqrt/exp/log/log10/pow), rounding
/// (ceil/floor/round/trunc). Trig/hyperbolic/pi/e are deferred — see
/// internal-wiki/FUTURE-STDLIB-API.md "Decimal + D32/D64/D128 transcendentals".
/// </summary>
public sealed class BigDecRuntimeTests
{
    private const string Lib = "razorforge_runtime";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_set_precision(int digits);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rf_bigdec_get_precision();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint rf_bigdec_new();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_free(nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint rf_bigdec_copy(nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_set_s64(nint a, long val);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_set_f64(nint a, double val);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_set_str(nint a,
        [MarshalAs(UnmanagedType.LPStr)] string str);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern long rf_bigdec_get_s64(nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern double rf_bigdec_get_f64(nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint rf_bigdec_get_str(nint a, int decimal_places);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rf_bigdec_cmp(nint a, nint b);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rf_bigdec_is_zero(nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rf_bigdec_is_neg(nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_add(nint result, nint a, nint b);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_sub(nint result, nint a, nint b);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_mul(nint result, nint a, nint b);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_div(nint result, int precision, nint a, nint b);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_neg(nint result, nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_abs(nint result, nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_sqrt(nint result, int precision, nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_pow(nint result, int precision, nint b, nint e);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_exp(nint result, int precision, nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_log(nint result, int precision, nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_log10(nint result, int precision, nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_ceil(nint result, nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_floor(nint result, nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_round(nint result, int decimal_places, nint a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rf_bigdec_trunc(nint result, int decimal_places, nint a);

    private static string GetStr(nint handle, int decimal_places = -1)
    {
        // The C side mallocs the buffer. Freeing it across the FFI boundary
        // requires either a runtime-exposed free helper (which we don't have
        // yet — TODO: rf_free_cstr) or P/Invoking libc free. Tests are short-
        // lived, so we accept the per-call leak rather than risk a mismatched-
        // allocator crash on Windows.
        nint cstr = rf_bigdec_get_str(handle, decimal_places);
        return Marshal.PtrToStringAnsi(cstr) ?? "";
    }

    private sealed class BigDec : IDisposable
    {
        public nint Handle { get; }
        public BigDec() { Handle = rf_bigdec_new(); }
        public static BigDec FromStr(string s)
        {
            var b = new BigDec();
            rf_bigdec_set_str(b.Handle, s);
            return b;
        }
        public void Dispose() { if (Handle != 0) rf_bigdec_free(Handle); }
    }

    [Fact]
    public void New_And_Free_DoesNotCrash()
    {
        nint a = rf_bigdec_new();
        Assert.NotEqual(0, a);
        rf_bigdec_free(a);
    }

    [Fact]
    public void Set_S64_RoundTrip()
    {
        using var a = new BigDec();
        rf_bigdec_set_s64(a.Handle, 42);
        Assert.Equal(42, rf_bigdec_get_s64(a.Handle));
    }

    [Fact]
    public void Set_F64_RoundTrip()
    {
        using var a = new BigDec();
        rf_bigdec_set_f64(a.Handle, 3.14159);
        Assert.Equal(3.14159, rf_bigdec_get_f64(a.Handle), precision: 5);
    }

    [Fact]
    public void Set_Str_Preserves_Precision_Beyond_F64()
    {
        // 25 significant digits — beyond F64 precision (~15-17).
        using var a = BigDec.FromStr("3.141592653589793238462643");
        string roundtrip = GetStr(a.Handle);
        Assert.Contains("3.141592653589793238462643", roundtrip);
    }

    [Fact]
    public void Add_Sub_Mul()
    {
        using var a = BigDec.FromStr("100");
        using var b = BigDec.FromStr("23");
        using var r = new BigDec();

        rf_bigdec_add(r.Handle, a.Handle, b.Handle);
        Assert.Equal(123, rf_bigdec_get_s64(r.Handle));

        rf_bigdec_sub(r.Handle, a.Handle, b.Handle);
        Assert.Equal(77, rf_bigdec_get_s64(r.Handle));

        rf_bigdec_mul(r.Handle, a.Handle, b.Handle);
        Assert.Equal(2300, rf_bigdec_get_s64(r.Handle));
    }

    [Fact]
    public void Div_Exact_Decimal()
    {
        // 1/4 = 0.25 exactly (no precision loss like binary).
        using var a = BigDec.FromStr("1");
        using var b = BigDec.FromStr("4");
        using var r = new BigDec();
        rf_bigdec_div(r.Handle, 50, a.Handle, b.Handle);
        Assert.Equal("0.25", GetStr(r.Handle));
    }

    [Fact]
    public void Div_Recurring_Honours_Precision()
    {
        using var a = BigDec.FromStr("1");
        using var b = BigDec.FromStr("3");
        using var r = new BigDec();
        rf_bigdec_div(r.Handle, 20, a.Handle, b.Handle);
        string s = GetStr(r.Handle);
        // Should start with "0.333333..." (decNumber rounds the last digit).
        Assert.StartsWith("0.3333333333", s);
    }

    [Fact]
    public void Neg_And_Abs()
    {
        using var a = BigDec.FromStr("7");
        using var r = new BigDec();
        rf_bigdec_neg(r.Handle, a.Handle);
        Assert.Equal(-7, rf_bigdec_get_s64(r.Handle));

        using var minus5 = BigDec.FromStr("-5");
        rf_bigdec_abs(r.Handle, minus5.Handle);
        Assert.Equal(5, rf_bigdec_get_s64(r.Handle));
    }

    [Fact]
    public void Cmp_IsZero_IsNeg()
    {
        using var minus3 = BigDec.FromStr("-3");
        using var zero = new BigDec();
        using var seven = BigDec.FromStr("7");

        Assert.Equal(-1, rf_bigdec_cmp(minus3.Handle, zero.Handle));
        Assert.Equal(0, rf_bigdec_cmp(zero.Handle, zero.Handle));
        Assert.Equal(1, rf_bigdec_cmp(seven.Handle, zero.Handle));

        Assert.Equal(1, rf_bigdec_is_zero(zero.Handle));
        Assert.Equal(0, rf_bigdec_is_zero(seven.Handle));

        Assert.Equal(1, rf_bigdec_is_neg(minus3.Handle));
        Assert.Equal(0, rf_bigdec_is_neg(zero.Handle));
        Assert.Equal(0, rf_bigdec_is_neg(seven.Handle));
    }

    [Fact]
    public void Sqrt_Exact()
    {
        using var a = BigDec.FromStr("144");
        using var r = new BigDec();
        rf_bigdec_sqrt(r.Handle, 20, a.Handle);
        Assert.Equal(12, rf_bigdec_get_s64(r.Handle));
    }

    [Fact]
    public void Pow_Integer_Exponent()
    {
        using var b = BigDec.FromStr("2");
        using var e = BigDec.FromStr("10");
        using var r = new BigDec();
        rf_bigdec_pow(r.Handle, 30, b.Handle, e.Handle);
        Assert.Equal(1024, rf_bigdec_get_s64(r.Handle));
    }

    [Fact]
    public void Exp_Log_Roundtrip()
    {
        using var one = BigDec.FromStr("1");
        using var expOne = new BigDec();
        using var lnExpOne = new BigDec();

        rf_bigdec_exp(expOne.Handle, 30, one.Handle);
        rf_bigdec_log(lnExpOne.Handle, 30, expOne.Handle);

        // ln(e^1) ≈ 1 within precision.
        double recovered = rf_bigdec_get_f64(lnExpOne.Handle);
        Assert.Equal(1.0, recovered, precision: 10);
    }

    [Fact]
    public void Log10_Of_1000()
    {
        using var thousand = BigDec.FromStr("1000");
        using var r = new BigDec();
        rf_bigdec_log10(r.Handle, 30, thousand.Handle);
        Assert.Equal(3.0, rf_bigdec_get_f64(r.Handle), precision: 10);
    }

    [Fact]
    public void Ceil_Floor()
    {
        using var pi = BigDec.FromStr("3.14159");
        using var r = new BigDec();

        rf_bigdec_ceil(r.Handle, pi.Handle);
        Assert.Equal(4, rf_bigdec_get_s64(r.Handle));

        rf_bigdec_floor(r.Handle, pi.Handle);
        Assert.Equal(3, rf_bigdec_get_s64(r.Handle));

        using var npi = BigDec.FromStr("-3.14159");
        rf_bigdec_ceil(r.Handle, npi.Handle);
        Assert.Equal(-3, rf_bigdec_get_s64(r.Handle));

        rf_bigdec_floor(r.Handle, npi.Handle);
        Assert.Equal(-4, rf_bigdec_get_s64(r.Handle));
    }

    [Fact]
    public void Round_To_DecimalPlaces()
    {
        using var pi = BigDec.FromStr("3.14159");
        using var r = new BigDec();
        rf_bigdec_round(r.Handle, 2, pi.Handle);
        Assert.Equal("3.14", GetStr(r.Handle));

        rf_bigdec_round(r.Handle, 4, pi.Handle);
        Assert.Equal("3.1416", GetStr(r.Handle));
    }

    [Fact]
    public void Trunc_Towards_Zero()
    {
        using var pi = BigDec.FromStr("3.78");
        using var r = new BigDec();
        rf_bigdec_trunc(r.Handle, 0, pi.Handle);
        Assert.Equal("3", GetStr(r.Handle));

        using var npi = BigDec.FromStr("-3.78");
        rf_bigdec_trunc(r.Handle, 0, npi.Handle);
        Assert.Equal("-3", GetStr(r.Handle));
    }

    [Fact]
    public void Copy_Is_Independent()
    {
        using var a = BigDec.FromStr("100");
        nint copyHandle = rf_bigdec_copy(a.Handle);
        try
        {
            using var ten = BigDec.FromStr("10");
            rf_bigdec_add(copyHandle, copyHandle, ten.Handle);
            Assert.Equal(110, rf_bigdec_get_s64(copyHandle));
            // Original must be untouched.
            Assert.Equal(100, rf_bigdec_get_s64(a.Handle));
        }
        finally
        {
            rf_bigdec_free(copyHandle);
        }
    }

    [Fact]
    public void Precision_GetSet_RoundTrip()
    {
        int saved = rf_bigdec_get_precision();
        try
        {
            rf_bigdec_set_precision(50);
            Assert.Equal(50, rf_bigdec_get_precision());
            rf_bigdec_set_precision(200);
            Assert.Equal(200, rf_bigdec_get_precision());
        }
        finally
        {
            rf_bigdec_set_precision(saved);
        }
    }

    [Fact]
    public void Precision_Clamped_To_Safe_Range()
    {
        int saved = rf_bigdec_get_precision();
        try
        {
            // Too-low — clamped up to 1.
            rf_bigdec_set_precision(0);
            Assert.Equal(1, rf_bigdec_get_precision());
            rf_bigdec_set_precision(-100);
            Assert.Equal(1, rf_bigdec_get_precision());

            // Too-high — clamped down (to DECNUMDIGITS=1000 OR DEC_MAX_MATH=999999,
            // whichever is smaller; both end up at 1000 in our build).
            rf_bigdec_set_precision(10_000_000);
            Assert.True(rf_bigdec_get_precision() <= 1000);
            Assert.True(rf_bigdec_get_precision() >= 1);
        }
        finally
        {
            rf_bigdec_set_precision(saved);
        }
    }

    [Fact]
    public void Precision_Affects_Default_Div()
    {
        int saved = rf_bigdec_get_precision();
        try
        {
            using var a = BigDec.FromStr("1");
            using var b = BigDec.FromStr("3");
            using var r = new BigDec();

            // Precision=10 → "0.3333333333" (10 threes after the point).
            rf_bigdec_set_precision(10);
            // Pass 0 as per-call precision to fall through to context default.
            rf_bigdec_div(r.Handle, 0, a.Handle, b.Handle);
            string s10 = GetStr(r.Handle);
            Assert.StartsWith("0.3333333333", s10);
            // Length sanity: not absurdly long.
            Assert.True(s10.Length < 20, $"too long for prec=10: '{s10}'");

            // Precision=50 → much longer result.
            rf_bigdec_set_precision(50);
            rf_bigdec_div(r.Handle, 0, a.Handle, b.Handle);
            string s50 = GetStr(r.Handle);
            Assert.True(s50.Length > 30, $"too short for prec=50: '{s50}'");
        }
        finally
        {
            rf_bigdec_set_precision(saved);
        }
    }
}