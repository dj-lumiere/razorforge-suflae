using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the S420 ImplicitWrapperCopy rule on variable declarations.
/// `var b = a` where `a` carries a non-trivially-copyable wrapper must spell out
/// the explicit verb (`steal` / `.retain()` / `.track()`). Trivially-copyable
/// values (primitives, Hijacked[T], primitive-only records) still copy bitwise.
/// </summary>
public class ImplicitWrapperCopyVarDeclTests
{
    /// <summary>Bare `var b = a` where `a: Retained[T]` is rejected.</summary>
    [Fact]
    public void Analyze_VarDecl_BareRetainedCopy_IsError()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var ra = a.retain()
                          var rb = ra
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "Implicit copy",
                comparisonType: StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains(value: "a.retain()",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>`var b = obj.field` where the field is `Retained[T]` is rejected.</summary>
    [Fact]
    public void Analyze_VarDecl_BorrowedMemberRetainedCopy_IsError()
    {
        string source = """
                        entity Node
                          value: S64

                        record Box
                          handle: Retained[Node]

                        routine start()
                          var a = Node(value: 1)
                          var b = Box(handle: a.retain())
                          var taken = b.handle
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "Implicit copy",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Explicit `.retain()` at the copy site is accepted.</summary>
    [Fact]
    public void Analyze_VarDecl_ExplicitRetain_IsAccepted()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var ra = a.retain()
                          var rb = ra.retain()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>Trivially-copyable record (all-primitive) bitwise copies — no error.</summary>
    [Fact]
    public void Analyze_VarDecl_TriviallyCopyableRecord_IsAccepted()
    {
        string source = """
                        record Point
                          x: S64
                          y: S64

                        routine start()
                          var p1 = Point(x: 1, y: 2)
                          var p2 = p1
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>Initializer that is a fresh call result (not a borrowed reference) is accepted.</summary>
    [Fact]
    public void Analyze_VarDecl_FreshCallResult_IsAccepted()
    {
        // `.retain()` returns a freshly-owned Retained handle — the var-decl rule only
        // fires on borrowed identifier / member references, not on call results.
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var ra = a.retain()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }
}
