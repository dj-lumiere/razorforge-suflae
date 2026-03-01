using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for flags type semantic validation.
/// Validates gaps #127-#135 from the compiler TODO.
/// </summary>
public class FlagsValidationTests
{
    #region Valid Flags (no errors expected)

    [Fact]
    public void Flags_SimpleDeclaration_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTooManyMembers);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsDuplicateMember);
    }

    [Fact]
    public void Flags_IsTest_ValidMember_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(perms: Permissions)
                          var result = perms is READ
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsMemberNotFound);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTypeMismatch);
    }

    [Fact]
    public void Flags_IsNotTest_ValidMember_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(perms: Permissions)
                          var result = perms isnot WRITE
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsMemberNotFound);
    }

    [Fact]
    public void Flags_IsOnlyWithAnd_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(perms: Permissions)
                          var result = perms isonly READ and WRITE
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsIsOnlyRejectsOrBut);
    }

    [Fact]
    public void Flags_ButOperator_SameType_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(a: Permissions, b: Permissions)
                          var result = a but b
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTypeMismatch);
    }

    #endregion

    #region #127: Max 64 members

    [Fact]
    public void Flags_MoreThan64Members_ReportsError()
    {
        string members = string.Join("\n  ", Enumerable.Range(0, 65).Select(i => $"FLAG_{i}"));
        string source = $$"""
                          flags TooMany
                            {{members}}
                          """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTooManyMembers);
    }

    [Fact]
    public void Flags_Exactly64Members_NoError()
    {
        string members = string.Join("\n  ", Enumerable.Range(0, 64).Select(i => $"FLAG_{i}"));
        string source = $$"""
                          flags Max64
                            {{members}}
                          """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTooManyMembers);
    }

    #endregion

    #region Duplicate members

    [Fact]
    public void Flags_DuplicateMember_ReportsError()
    {
        string source = """
                        flags Perms
                          READ
                          WRITE
                          READ
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsDuplicateMember);
    }

    #endregion

    #region #128: or in assignment

    [Fact]
    public void Flags_OrInAssignment_ReportsError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test(a: Permissions, b: Permissions)
                          var combined = a or b
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsOrInAssignment);
    }

    #endregion

    #region #129: Flags when requires else

    [Fact]
    public void Flags_WhenExpressionWithoutElse_ReportsError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test(perms: Permissions)
                          var desc = when perms
                            is READ => "read"
                            is WRITE => "write"
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    [Fact]
    public void Flags_WhenExpressionWithElse_NoError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test(perms: Permissions)
                          var desc = when perms
                            is READ => "read"
                            else => "other"
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    #endregion

    #region #133: isonly rejects or/but (parser-enforced)

    // #133 is enforced at the parser level: the isonly parser only accepts 'and' connective.
    // 'isonly READ or WRITE' parses as '(perms isonly READ) or WRITE' — a logical or,
    // which produces LogicalOperatorRequiresBool. No semantic check needed.

    [Fact]
    public void Flags_IsOnlyWithOr_ProducesParseError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test(perms: Permissions)
                          var result = perms isonly READ or WRITE
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        // Parser treats 'or' after isonly as logical or, not flags connective
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.LogicalOperatorRequiresBool);
    }

    #endregion

    #region #134: No arithmetic on flags

    [Fact]
    public void Flags_Arithmetic_ReportsError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test(a: Permissions, b: Permissions)
                          var result = a + b
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnFlagsType);
    }

    #endregion

    #region #135: No custom operators on flags

    [Fact]
    public void Flags_CustomOperator_ReportsError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        @readonly
                        routine Permissions.__add__(you: Permissions) -> Permissions
                          return me
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsCustomOperatorNotAllowed);
    }

    #endregion

    #region Flag member validation

    [Fact]
    public void Flags_IsTest_UnknownMember_ReportsError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test(perms: Permissions)
                          var result = perms is EXECUTE
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsMemberNotFound);
    }

    [Fact]
    public void Flags_IsTestOnNonFlags_ReportsError()
    {
        string source = """
                        routine test(x: S32)
                          var result = x is READ
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        // 'READ' is not a type, so this produces UnknownType (not FlagsTypeMismatch)
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownType);
    }

    [Fact]
    public void Flags_ButOperator_TypeMismatch_ReportsError()
    {
        string source = """
                        flags Perms
                          READ
                          WRITE

                        flags Roles
                          ADMIN
                          USER

                        routine test(p: Perms, r: Roles)
                          var result = p but r
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTypeMismatch);
    }

    #endregion
}
