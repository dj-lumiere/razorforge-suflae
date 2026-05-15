using System;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the S420 ImplicitWrapperCopy rule applied to function arguments.
/// `foo(a)` where the parameter type is non-trivially-copyable must spell out the
/// explicit verb at the call site, just like an assignment would.
/// </summary>
public class ImplicitWrapperCopyCallArgTests
{
    /// <summary>Passing a Retained variable by name to a Retained-typed parameter is rejected.</summary>
    [Fact]
    public void Analyze_CallArg_BareRetained_IsError()
    {
        string source = """
                        entity Node
                          value: S64

                        routine consume(handle: Retained[Node])
                          return

                        routine start()
                          var a = Node(value: 1)
                          var ra = a.retain()
                          consume(handle: ra)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy &&
                e.Message.Contains(value: "in call",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Passing via `.retain()` at the call site is accepted.</summary>
    [Fact]
    public void Analyze_CallArg_ExplicitRetain_IsAccepted()
    {
        string source = """
                        entity Node
                          value: S64

                        routine consume(handle: Retained[Node])
                          return

                        routine start()
                          var a = Node(value: 1)
                          var ra = a.retain()
                          consume(handle: ra.retain())
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>Passing a primitive (trivially copyable) is accepted.</summary>
    [Fact]
    public void Analyze_CallArg_Primitive_IsAccepted()
    {
        string source = """
                        routine consume(x: S64)
                          return

                        routine start()
                          var a = 42_s64
                          consume(x: a)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }
}
