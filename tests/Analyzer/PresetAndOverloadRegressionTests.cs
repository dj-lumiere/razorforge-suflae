using Builder.Tokenizer;
using Builder.Verification;
using Builder.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Regression locks for builder bugs found while porting CORE-MATH: aggregate preset elements that
/// reached the emitter untyped, a free secret routine rejected from its own module, and bare-literal
/// calls that bound whichever overload registered first.
/// </summary>
public class PresetAndOverloadRegressionTests
{
    /// <summary>
    /// An unsuffixed literal in an <c>Array[B32, N]</c> preset takes the element type. The preset becomes one
    /// constant global that never passes through lowering, so an undecided literal used to reach the emitter
    /// and be emitted as a function-local temporary (invalid IR).
    /// </summary>
    [Fact]
    public void FloatArrayPreset_UnsuffixedElements_TakeElementType()
    {
        Program program = Parse(source: """
                                        preset SINGLES: Array[B32, 2] = [1.0, 0.25]
                                        preset WORDS: Array[U16, 2] = [1, 65535]

                                        routine start()
                                          var a = SINGLES[0]
                                          var b = WORDS[1]
                                          return
                                        """);
        AnalysisResult result = new SemanticVerifier(language: Language.RazorForge).Analyze(program: program);
        Assert.Empty(collection: result.Errors);

        List<PresetDeclaration> presets = GetDeclarations<PresetDeclaration>(program: program);
        var singles = (ListLiteralExpression)presets.Single(predicate: p => p.Name == "SINGLES").Value;
        var words = (ListLiteralExpression)presets.Single(predicate: p => p.Name == "WORDS").Value;
        Assert.All(collection: singles.Elements,
            action: e => Assert.Equal(expected: TokenType.B32Literal, actual: ((LiteralExpression)e).LiteralType));
        Assert.All(collection: words.Elements,
            action: e => Assert.Equal(expected: TokenType.U16Literal, actual: ((LiteralExpression)e).LiteralType));
    }

    /// <summary>An out-of-range preset element is reported against the element type (it used to pass).</summary>
    [Fact]
    public void ArrayPreset_ElementOutOfRange_IsReported()
    {
        AssertHasError(source: """
                               preset BYTES: Array[U8, 2] = [1, 300]

                               routine start()
                                 var a = BYTES[0]
                                 return
                               """,
            expectedErrorSubstring: "overflows type 'U8'");
    }

    /// <summary>A preset element of the wrong kind is reported (it used to crash the emitter).</summary>
    [Fact]
    public void ArrayPreset_ElementOfWrongKind_IsReported()
    {
        AssertHasError(source: """
                               preset VALUES: Array[B64, 2] = [1.0, "x"]

                               routine start()
                                 var a = VALUES[0]
                                 return
                               """,
            expectedErrorSubstring: "is a 'Text' literal");
    }

    /// <summary>A free <c>secret routine</c> is callable from its own module (it used to be RF-S403).</summary>
    [Fact]
    public void SecretFreeRoutine_CallableFromOwnModule()
    {
        AssertAnalyzes(source: """
                               secret routine helper(x: S64) -> S64
                                 return x + 1

                               routine start()
                                 var y = helper(x: 41)
                                 return
                               """);
    }

    /// <summary>
    /// A call whose arguments are all bare literals binds the overload for the literals' default type
    /// (B64). It used to bind the first-registered overload (B128 for <c>hypot_unchecked</c>), so assigning
    /// the result to a B64 was a type mismatch.
    /// </summary>
    [Fact]
    public void BareLiteralArguments_PickDefaultTypeOverload()
    {
        AssertAnalyzes(source: """
                               routine start()
                                 var h: B64 = hypot_unchecked(x: 0.1, y: 0.2)
                                 var t: B64 = atan2(y: 1.0, x: 2.0)
                                 return
                               """);
    }

    /// <summary>
    /// An explicit generic call checks its arguments against the substituted parameters. They were only typed
    /// against the expected type, so any argument was accepted (and an array passed where
    /// `LLVM::load_element_ref` wants a `Hijacked` pointer reached the emitter as invalid IR).
    /// </summary>
    [Fact]
    public void ExplicitGenericCall_ArgumentTypeMismatch_IsReported()
    {
        AssertHasError(source: """
                               routine probe[C](p: C) -> U64
                                 return 0_u64

                               routine start()
                                 var r = probe[Text](p: 5_s64)
                                 return
                               """,
            expectedErrorSubstring: "cannot convert 'S64' to 'Text'");
    }

    [Fact]
    public void LoadElementRef_OnAPresetValue_IsReported()
    {
        AssertHasError(source: """
                               preset TAB: Array[U64, 2] = [1_u64, 2_u64]

                               routine start()
                                 danger
                                   var r = LLVM::load_element_ref[Array[U64, 2], U64](TAB, 0_u64)
                                 return
                               """,
            expectedErrorSubstring: "cannot convert 'Array[Core.U64, 2]' to 'Hijacked[Core.Array[Core.U64, 2]]'");
    }
}
