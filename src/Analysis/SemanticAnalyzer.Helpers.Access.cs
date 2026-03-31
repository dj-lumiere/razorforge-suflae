namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using Compiler.Lexer;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    private bool IsInlineOnlyTokenType(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return InlineOnlyTokenTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the token kind for display in error messages.
    /// </summary>
    private static string GetTokenKindDescription(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return baseName switch
        {
            "Viewed" => "read-only token (Viewed)",
            "Hijacked" => "exclusive write token (Hijacked)",
            "Inspected" => "shared read token (Inspected)",
            "Seized" => "exclusive shared write token (Seized)",
            _ => "token"
        };
    }

    /// <summary>
    /// Validates that a type is not an inline-only token when used as a return type.
    /// </summary>
    private void ValidateNotTokenReturnType(TypeSymbol type, SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(code: SemanticDiagnosticCode.TokenReturnNotAllowed,
                message:
                $"Cannot return {GetTokenKindDescription(type: type)} from a routine. Tokens are inline-only and cannot escape their scope.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that a type is not an inline-only token when used as a member variable type.
    /// </summary>
    private void ValidateNotTokenMemberVariableType(TypeSymbol type, string memberVariableName,
        SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(code: SemanticDiagnosticCode.TokenMemberVariableNotAllowed,
                message:
                $"Cannot store {GetTokenKindDescription(type: type)} in member variable '{memberVariableName}'. Tokens are inline-only and cannot be stored.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that exclusive tokens (Hijacked, Seized) are not passed multiple times in a single call.
    /// </summary>
    private void ValidateExclusiveTokenUniqueness(List<Expression> arguments,
        SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        // Track which exclusive token expressions we've seen
        var seenExclusiveTokens = new HashSet<string>();

        foreach (Expression arg in arguments)
        {
            // Get the expression's type
            if (arg.ResolvedType == null)
            {
                continue;
            }

            // Convert AST TypeInfo back to get the type name
            string typeName = arg.ResolvedType.Name;
            string baseName = GetBaseTypeName(typeName: typeName);

            if (!ExclusiveTokenTypes.Contains(value: baseName))
            {
                continue;
            }

            // Get a string representation of the expression for uniqueness checking
            string exprKey = GetExpressionKey(expression: arg);
            if (string.IsNullOrEmpty(value: exprKey))
            {
                continue;
            }

            if (seenExclusiveTokens.Contains(value: exprKey))
            {
                ReportError(code: SemanticDiagnosticCode.ExclusiveTokenDuplicate,
                    message:
                    $"Cannot pass the same {baseName} token '{exprKey}' multiple times in a single call. Exclusive tokens require unique access.",
                    location: location);
            }
            else
            {
                seenExclusiveTokens.Add(item: exprKey);
            }
        }
    }

    /// <summary>
    /// Gets a string key representing an expression for uniqueness checking.
    /// Returns null for complex expressions that can't be easily tracked.
    /// </summary>
    private static string? GetExpressionKey(Expression expression)
    {
        return expression switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression member =>
                $"{GetExpressionKey(expression: member.Object)}.{member.PropertyName}",
            _ => null
        };
    }

    /// <summary>
    /// Validates that a type is not an inline-only token when used as a variant case payload.
    /// </summary>
    private void ValidateNotTokenVariantPayload(TypeSymbol type, string caseName,
        SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(code: SemanticDiagnosticCode.TokenVariantPayloadNotAllowed,
                message:
                $"Cannot use {GetTokenKindDescription(type: type)} as payload for variant case '{caseName}'. Tokens are inline-only and cannot be stored in variants.",
                location: location);
        }
    }

    /// <summary>
    /// Gets the effective visibility for member variable write access.
    /// For posted member variables, write access is secret (only owner can write).
    /// </summary>
    /// <param name="memberVariable">The member variable to check.</param>
    /// <returns>The effective visibility for write access.</returns>
    private static VisibilityModifier GetEffectiveWriteVisibility(
        MemberVariableInfo memberVariable)
    {
        // Posted member variables have open read but secret write
        return memberVariable.Visibility == VisibilityModifier.Posted
            ? VisibilityModifier.Secret
            : memberVariable.Visibility;
    }

    /// <summary>
    /// Checks if access to a member variable is allowed from the current context.
    /// </summary>
    /// <param name="memberVariable">The member variable being accessed.</param>
    /// <param name="isWrite">Whether this is a write access (assignment).</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateMemberVariableAccess(MemberVariableInfo memberVariable, bool isWrite,
        SourceLocation accessLocation)
    {
        // Posted member variables: open read, module-only write
        if (isWrite && memberVariable.Visibility == VisibilityModifier.Posted &&
            !IsAccessingFromSameModule(memberModule: memberVariable.Owner?.Module))
        {
            string typeName = memberVariable.Owner?.Name ?? "type";
            ReportError(code: SemanticDiagnosticCode.PostedMemberAccess,
                message:
                $"Cannot write to posted member variable '{memberVariable.Name}' of '{typeName}' from outside its module.",
                location: accessLocation);
            return;
        }

        // For posted member variables, write access is restricted to secret (module only)
        VisibilityModifier visibility = isWrite
            ? GetEffectiveWriteVisibility(memberVariable: memberVariable)
            : memberVariable.Visibility;

        ValidateMemberAccess(visibility: visibility,
            memberKind: "member variable",
            memberName: memberVariable.Name,
            ownerType: memberVariable.Owner,
            accessLocation: accessLocation);
    }

    /// <summary>
    /// Checks if access to a routine is allowed from the current context.
    /// </summary>
    /// <param name="routine">The routine being accessed.</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateRoutineAccess(RoutineInfo routine, SourceLocation accessLocation)
    {
        ValidateMemberAccess(visibility: routine.Visibility,
            memberKind: routine.Kind switch
            {
                RoutineKind.Creator => "creator",
                RoutineKind.MemberRoutine => "member routine",
                _ => "routine"
            },
            memberName: routine.Name,
            ownerType: routine.OwnerType,
            accessLocation: accessLocation);

        // Dangerous routines can only be called inside danger! blocks
        if (routine.IsDangerous && !InDangerBlock)
        {
            ReportError(code: SemanticDiagnosticCode.DangerousCallOutsideDangerBlock,
                message:
                $"Dangerous routine '{routine.Name}' can only be called inside a 'danger!' block.",
                location: accessLocation);
        }
    }

    /// <summary>
    /// Validates access to a member based on visibility rules.
    /// </summary>
    /// <param name="visibility">The visibility modifier of the member.</param>
    /// <param name="memberKind">The kind of member (member variable, method, etc.) for error messages.</param>
    /// <param name="memberName">The name of the member.</param>
    /// <param name="ownerType">The type that owns this member, if any.</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateMemberAccess(VisibilityModifier visibility, string memberKind,
        string memberName, TypeSymbol? ownerType, SourceLocation accessLocation)
    {
        switch (visibility)
        {
            case VisibilityModifier.Secret:
                // Secret members are accessible within the same module
                if (!IsAccessingFromSameModule(memberModule: ownerType?.Module))
                {
                    string typeName = ownerType?.Name ?? "type";
                    ReportError(code: SemanticDiagnosticCode.SecretMemberAccess,
                        message:
                        $"Cannot access secret {memberKind} '{memberName}' of '{typeName}' from outside its module.",
                        location: accessLocation);
                }

                break;

            case VisibilityModifier.Posted:
            case VisibilityModifier.Open:
            case VisibilityModifier.External:
                // Open/Posted/External members are accessible from anywhere for reading
                break;
        }
    }

    /// <summary>
    /// Checks if the current access context is within the same module as the member.
    /// Module comparison is exact (sub-modules are different modules).
    /// </summary>
    private bool IsAccessingFromSameModule(string? memberModule)
    {
        string? currentModuleName = GetCurrentModuleName();

        // If both are in no module, they're in the same module
        if (string.IsNullOrEmpty(value: memberModule) &&
            string.IsNullOrEmpty(value: currentModuleName))
        {
            return true;
        }

        // If either is null/empty but not both, they're not in the same module
        if (string.IsNullOrEmpty(value: memberModule) ||
            string.IsNullOrEmpty(value: currentModuleName))
        {
            return false;
        }

        // Module comparison is exact - sub-modules are different modules
        return currentModuleName == memberModule;
    }

    /// <summary>
    /// Validates write access to a member variable, checking setter visibility.
    /// </summary>
    /// <param name="objectType">The type of the object being accessed.</param>
    /// <param name="memberVariableName">The name of the member variable being written.</param>
    /// <param name="location">The source location of the write.</param>
    private void ValidateMemberVariableWriteAccess(TypeSymbol objectType,
        string memberVariableName, SourceLocation location)
    {
        MemberVariableInfo? memberVariable = objectType switch
        {
            RecordTypeInfo record => record.LookupMemberVariable(
                memberVariableName: memberVariableName),
            EntityTypeInfo entity => entity.LookupMemberVariable(
                memberVariableName: memberVariableName),
            _ => null
        };

        if (memberVariable != null)
        {
            ValidateMemberVariableAccess(memberVariable: memberVariable,
                isWrite: true,
                accessLocation: location);
        }
    }

    /// <summary>
    /// Checks whether a file path is inside the stdlib directory.
    /// Used to allow stdlib files to use reserved features (e.g., module Core).
    /// </summary>
    private bool IsStdlibFile(string filePath)
    {
        string? stdlibPath = _registry.StdlibPath;
        if (string.IsNullOrEmpty(value: stdlibPath) || string.IsNullOrEmpty(value: filePath))
        {
            return false;
        }

        string normalizedFile = Path.GetFullPath(path: filePath);
        string normalizedStdlib = Path.GetFullPath(path: stdlibPath);
        return normalizedFile.StartsWith(value: normalizedStdlib,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }
}
