using TypeModel.Enums;
using TypeModel.Types;

namespace Compiler.Resolution;

using TypeInfo = TypeInfo;

public sealed partial class TypeRegistry
{
    #region Language-Specific Validation

    /// <summary>
    /// Validates that a type is allowed for the current language.
    /// </summary>
    /// <param name="type">The type to validate.</param>
    /// <returns>True if the type is allowed for the current language, false otherwise.</returns>
    public bool IsTypeAllowedForLanguage(TypeInfo type)
    {
        // Memory wrapper types are RazorForge only
        if (IsMemoryWrapperType(typeName: type.Name) && Language == Language.Suflae)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a type name is a RazorForge-specific memory wrapper type.
    /// </summary>
    /// <param name="typeName">The name of the type to check.</param>
    /// <returns>True if the type is a memory wrapper, false otherwise.</returns>
    private static bool IsMemoryWrapperType(string typeName)
    {
        return typeName is Compiler.Resolution.RuntimeContract.Viewing or Compiler.Resolution.RuntimeContract.Modifying or Compiler.Resolution.RuntimeContract.Inspecting or Compiler.Resolution.RuntimeContract.Claiming or Compiler.Resolution.RuntimeContract.Hijacked
            or Compiler.Resolution.RuntimeContract.Shared or Compiler.Resolution.RuntimeContract.Watched or Compiler.Resolution.RuntimeContract.Retained or Compiler.Resolution.RuntimeContract.Tracked;
    }

    #endregion
}
