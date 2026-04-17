using SemanticVerification.Results;
using Compiler.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for protocol validation rules:
/// #61: Protocol mutation contract violation
/// </summary>
public class ProtocolValidationTests
{
    #region #61: Protocol mutation contract violation
    /// <summary>
    /// Tests Analyze_ProtocolReadonlyImplReadonly_NoError.
    /// </summary>

    [Fact]
    public void Analyze_ProtocolReadonlyImplReadonly_NoError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text
                        record Foo obeys Displayable
                          value: S32
                        @readonly
                        routine Foo.display() -> Text
                          return "foo"
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMutationContractViolation);
    }
    /// <summary>
    /// Tests Analyze_ProtocolReadonlyImplWritable_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ProtocolReadonlyImplWritable_ReportsError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text
                        record Bar obeys Displayable
                          value: S32
                        @writable
                        routine Bar.display() -> Text
                          return "bar"
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMutationContractViolation);
    }
    /// <summary>
    /// Tests Analyze_ProtocolWritableImplReadonly_NoError.
    /// </summary>

    [Fact]
    public void Analyze_ProtocolWritableImplReadonly_NoError()
    {
        string source = """
                        protocol Mutator
                          @writable
                          routine Me.mutate()
                        record Baz obeys Mutator
                          value: S32
                        @readonly
                        routine Baz.mutate()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMutationContractViolation);
    }
    /// <summary>
    /// Tests Analyze_ProtocolWritableImplMigratable_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ProtocolWritableImplMigratable_ReportsError()
    {
        string source = """
                        protocol Mutator
                          @writable
                          routine Me.mutate()
                        entity Thing obeys Mutator
                          value: S32
                        routine Thing.mutate()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMutationContractViolation);
    }

    #endregion
}
