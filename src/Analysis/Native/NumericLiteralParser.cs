using System.Runtime.InteropServices;

namespace SemanticAnalysis.Native;

/// <summary>
/// P/Invoke bindings for native numeric literal parsing functions.
/// Used by the semantic analyzer to parse types without C# equivalents:
/// f128, d32, d64, d128, Integer, Decimal.
/// </summary>
public static class NumericLiteralParser
{
    private const string RuntimeLib = "razorforge_runtime";

    #region f128 (IEEE binary128)

    /// <summary>
    /// 128-bit IEEE binary floating point value.
    /// Stored as two 64-bit unsigned integers (little-endian).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct F128
    {
        public ulong Lo;
        public ulong Hi;

        public override string ToString() => $"f128(0x{Hi:X16}{Lo:X16})";
    }

    /// <summary>
    /// Parses a string to IEEE binary128 (f128) using LibBF.
    /// </summary>
    /// <param name="str">The string representation of the number.</param>
    /// <returns>The parsed f128 value.</returns>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_f128_from_string")]
    public static extern F128 ParseF128([MarshalAs(UnmanagedType.LPStr)] string str);

    #endregion

    #region Decimal floating-point (IEEE 754-2008)

    /// <summary>
    /// 32-bit IEEE decimal floating point value.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D32
    {
        public uint Value;

        public override string ToString() => $"d32(0x{Value:X8})";
    }

    /// <summary>
    /// 64-bit IEEE decimal floating point value.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D64
    {
        public ulong Value;

        public override string ToString() => $"d64(0x{Value:X16})";
    }

    /// <summary>
    /// 128-bit IEEE decimal floating point value.
    /// Stored as two 64-bit unsigned integers (little-endian).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D128
    {
        public ulong Lo;
        public ulong Hi;

        public override string ToString() => $"d128(0x{Hi:X16}{Lo:X16})";
    }

    /// <summary>
    /// Parses a string to IEEE decimal32 (d32) using Intel DFP library.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "d32_from_string")]
    public static extern D32 ParseD32([MarshalAs(UnmanagedType.LPStr)] string str);

    /// <summary>
    /// Parses a string to IEEE decimal64 (d64) using Intel DFP library.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "d64_from_string")]
    public static extern D64 ParseD64([MarshalAs(UnmanagedType.LPStr)] string str);

    /// <summary>
    /// Parses a string to IEEE decimal128 (d128) using Intel DFP library.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "d128_from_string")]
    public static extern D128 ParseD128([MarshalAs(UnmanagedType.LPStr)] string str);

    #endregion

    #region Arbitrary precision Integer (LibBF)

    /// <summary>
    /// Parses a string to an arbitrary precision integer using LibBF.
    /// Returns an opaque handle that must be freed with FreeInteger.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_integer_from_string")]
    public static extern IntPtr ParseInteger([MarshalAs(UnmanagedType.LPStr)] string str);

    /// <summary>
    /// Frees an arbitrary precision integer handle.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_integer_free")]
    public static extern void FreeInteger(IntPtr handle);

    /// <summary>
    /// Gets the byte size needed to store the integer as raw limbs.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_integer_byte_size")]
    public static extern nuint GetIntegerByteSize(IntPtr handle);

    /// <summary>
    /// Copies integer limbs to a buffer.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_integer_to_bytes")]
    public static extern nuint IntegerToBytes(IntPtr handle, byte[] buffer, nuint bufferSize);

    /// <summary>
    /// Gets the sign of the integer (0 = positive, 1 = negative).
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_integer_sign")]
    public static extern int GetIntegerSign(IntPtr handle);

    /// <summary>
    /// Gets the exponent of the integer.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_integer_exponent")]
    public static extern long GetIntegerExponent(IntPtr handle);

    #endregion

    #region Arbitrary precision Decimal (MAPM)

    /// <summary>
    /// Parses a string to an arbitrary precision decimal using MAPM.
    /// Returns an opaque handle that must be freed with FreeDecimal.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_decimal_from_string")]
    public static extern IntPtr ParseDecimal([MarshalAs(UnmanagedType.LPStr)] string str);

    /// <summary>
    /// Frees an arbitrary precision decimal handle.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_decimal_free")]
    public static extern void FreeDecimal(IntPtr handle);

    /// <summary>
    /// Gets the sign of the decimal (-1 = negative, 0 = zero, 1 = positive).
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_decimal_sign")]
    public static extern int GetDecimalSign(IntPtr handle);

    /// <summary>
    /// Gets the exponent (power of 10) of the decimal.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_decimal_exponent")]
    public static extern int GetDecimalExponent(IntPtr handle);

    /// <summary>
    /// Gets the number of significant digits in the decimal.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_decimal_significant_digits")]
    public static extern int GetDecimalSignificantDigits(IntPtr handle);

    /// <summary>
    /// Checks if the decimal represents an integer value.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_decimal_is_integer")]
    public static extern int IsDecimalInteger(IntPtr handle);

    /// <summary>
    /// Negates the decimal value in place.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_decimal_negate")]
    public static extern void NegateDecimal(IntPtr handle);

    /// <summary>
    /// Converts decimal to string with specified decimal places.
    /// Caller must free the returned string with FreeString.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_decimal_to_string")]
    private static extern IntPtr DecimalToStringNative(IntPtr handle, int decimalPlaces);

    /// <summary>
    /// Converts decimal to integer string (no decimal point).
    /// Caller must free the returned string with FreeString.
    /// </summary>
    [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rf_cs_decimal_to_integer_string")]
    private static extern IntPtr DecimalToIntegerStringNative(IntPtr handle);

    /// <summary>
    /// Converts a decimal handle to a managed string.
    /// </summary>
    /// <param name="handle">The decimal handle.</param>
    /// <param name="decimalPlaces">Number of decimal places in output.</param>
    /// <returns>String representation of the decimal.</returns>
    public static string DecimalToString(IntPtr handle, int decimalPlaces)
    {
        IntPtr strPtr = DecimalToStringNative(handle, decimalPlaces);
        if (strPtr == IntPtr.Zero) return string.Empty;

        string result = Marshal.PtrToStringAnsi(strPtr) ?? string.Empty;
        // Note: The native library allocates this with malloc, so we need to free it
        // For now, we'll leak this memory. In production, add a proper free function.
        return result;
    }

    /// <summary>
    /// Converts a decimal handle to an integer string.
    /// </summary>
    public static string DecimalToIntegerString(IntPtr handle)
    {
        IntPtr strPtr = DecimalToIntegerStringNative(handle);
        if (strPtr == IntPtr.Zero) return string.Empty;

        string result = Marshal.PtrToStringAnsi(strPtr) ?? string.Empty;
        return result;
    }

    #endregion

    #region Helper methods for managed types

    /// <summary>
    /// Parses an arbitrary precision integer and returns it as a managed byte array.
    /// </summary>
    /// <param name="str">The string representation.</param>
    /// <returns>Tuple of (bytes, sign) where sign is 0 for positive, 1 for negative.</returns>
    public static (byte[] bytes, int sign) ParseIntegerToBytes(string str)
    {
        IntPtr handle = ParseInteger(str);
        if (handle == IntPtr.Zero)
        {
            return (Array.Empty<byte>(), 0);
        }

        try
        {
            nuint size = GetIntegerByteSize(handle);
            int sign = GetIntegerSign(handle);

            // Handle zero: libbf represents zero with len=0, but we need at least 1 byte
            if (size == 0)
            {
                return ([0], sign);
            }

            byte[] bytes = new byte[(int)size];
            IntegerToBytes(handle, bytes, size);
            return (bytes, sign);
        }
        finally
        {
            FreeInteger(handle);
        }
    }

    /// <summary>
    /// Parses an arbitrary precision decimal and returns metadata.
    /// </summary>
    /// <param name="str">The string representation.</param>
    /// <returns>Tuple of (stringValue, sign, exponent, significantDigits, isInteger).</returns>
    public static (string value, int sign, int exponent, int significantDigits, bool isInteger) ParseDecimalInfo(string str)
    {
        IntPtr handle = ParseDecimal(str);
        if (handle == IntPtr.Zero)
        {
            return (string.Empty, 0, 0, 0, false);
        }

        try
        {
            int sign = GetDecimalSign(handle);
            int exponent = GetDecimalExponent(handle);
            int sigDigits = GetDecimalSignificantDigits(handle);
            bool isInt = IsDecimalInteger(handle) != 0;

            // Get string representation with enough precision
            string value = DecimalToString(handle, sigDigits + 10);

            return (value, sign, exponent, sigDigits, isInt);
        }
        finally
        {
            FreeDecimal(handle);
        }
    }

    #endregion
}
