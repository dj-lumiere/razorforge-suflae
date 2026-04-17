using System.Text;

namespace Compiler.Desugaring;

/// <summary>
/// FNV-1a type ID computation shared across desugaring passes and codegen.
/// Must stay in sync with <c>LLVMCodeGenerator.ComputeTypeId</c>.
/// </summary>
internal static class TypeIdHelper
{
    /// <summary>
    /// Computes the FNV-1a hash of <paramref name="fullName"/> as a type identifier.
    /// Returns 0 for <c>Blank</c> (the reserved absent sentinel).
    /// Returns 1 if the hash would otherwise be 0 (reserved for Blank).
    /// </summary>
    internal static ulong ComputeTypeId(string fullName)
    {
        if (fullName is "Blank" || fullName.EndsWith(value: ".Blank"))
            return 0UL;
        ulong hash = 14695981039346656037UL; // FNV-1a offset basis
        foreach (byte b in Encoding.UTF8.GetBytes(s: fullName))
        {
            hash ^= b;
            hash *= 1099511628211UL; // FNV-1a prime
        }
        return hash == 0UL ? 1UL : hash;
    }
}