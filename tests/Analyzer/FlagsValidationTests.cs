using SemanticVerification.Results;
using Compiler.Diagnostics;
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
    /// <summary>
    /// Tests Flags_SimpleDeclaration_NoErrors.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_IsTest_ValidMember_NoErrors.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_IsNotTest_ValidMember_NoErrors.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_IsOnlyWithAnd_NoErrors.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_ButOperator_SameType_NoErrors.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_AndCombiner_SameType_NoErrors.
    /// </summary>

    [Fact]
    public void Flags_AndCombiner_SameType_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(a: Permissions, b: Permissions)
                          var combined = a and b
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.LogicalOperatorRequiresBool);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTypeMismatch);
    }
    /// <summary>
    /// Tests Flags_AndCombiner_DifferentTypes_ReportsError.
    /// </summary>

    [Fact]
    public void Flags_AndCombiner_DifferentTypes_ReportsError()
    {
        string source = """
                        flags Perms
                          READ
                          WRITE

                        flags Roles
                          ADMIN
                          USER

                        routine test(p: Perms, r: Roles)
                          var combined = p and r
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.LogicalOperatorRequiresBool);
    }
    /// <summary>
    /// Tests Flags_AllOnAllOff_NoErrors.
    /// </summary>

    [Fact]
    public void Flags_AllOnAllOff_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test()
                          var all = Permissions.all_on()
                          var none = Permissions.all_off()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MethodNotFound);
    }

    #endregion

    #region #127: Max 64 members
    /// <summary>
    /// Tests Flags_MoreThan64Members_ReportsError.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_Exactly64Members_NoError.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_DuplicateMember_ReportsError.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_OrInAssignment_ReportsError.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_WhenExpressionWithoutElse_ReportsError.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_WhenExpressionWithElse_NoError.
    /// </summary>

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
    // 'isonly READ or WRITE' parses as '(perms isonly READ) or WRITE' ??a logical or,
    // which produces LogicalOperatorRequiresBool. No semantic check needed.
    /// <summary>
    /// Tests Flags_IsOnlyWithOr_ProducesParseError.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_Arithmetic_ReportsError.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_CustomOperator_ReportsError.
    /// </summary>

    [Fact]
    public void Flags_CustomOperator_ReportsError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        @readonly
                        routine Permissions.$add(you: Permissions) -> Permissions
                          return me
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsCustomOperatorNotAllowed);
    }

    #endregion

    #region Flag member validation
    /// <summary>
    /// Tests Flags_IsTest_UnknownMember_ReportsError.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_IsTestOnNonFlags_ReportsError.
    /// </summary>

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
    /// <summary>
    /// Tests Flags_ButOperator_TypeMismatch_ReportsError.
    /// </summary>

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

    #region Member Access (C98)
    /// <summary>
    /// Tests Flags_MemberAccess_AsValue.
    /// </summary>

    [Fact]
    public void Flags_MemberAccess_AsValue()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test()
                          var f = Permissions.READ
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsMemberNotFound);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }
    /// <summary>
    /// Tests Flags_MemberAccess_InvalidMember.
    /// </summary>

    [Fact]
    public void Flags_MemberAccess_InvalidMember()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test()
                          var f = Permissions.EXECUTE
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
    }

    #endregion
}
