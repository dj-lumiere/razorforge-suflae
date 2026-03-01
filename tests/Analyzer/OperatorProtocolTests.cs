using SemanticAnalysis.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for operator protocol enforcement (RF-S411):
/// - Types must follow required protocol to define operator methods
/// - Correct protocol conformance allows operator definitions
/// - Missing protocol conformance reports error
/// </summary>
public class OperatorProtocolTests
{
    #region Correct Protocol Conformance

    [Fact]
    public void Analyze_AddableWithFollows_NoError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.__add__(you: Me) -> Me

                        record Vector obeys Addable
                          x: S32
                          y: S32

                        @readonly
                        routine Vector.__add__(you: Vector) -> Vector
                          return Vector(x: me.x + you.x, y: me.y + you.y)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_EquatableWithFollows_NoError()
    {
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.__eq__(you: Me) -> Bool

                        record Point obeys Equatable
                          x: S32
                          y: S32

                        @readonly
                        routine Point.__eq__(you: Point) -> Bool
                          return me.x == you.x and me.y == you.y
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_ComparableWithFollows_NoError()
    {
        string source = """
                        choice ComparisonSign
                          ME_SMALL
                          SAME
                          ME_LARGE

                        protocol Comparable
                          @readonly
                          routine Me.__cmp__(you: Me) -> ComparisonSign

                        record Score obeys Comparable
                          value: S32

                        @readonly
                        routine Score.__cmp__(you: Score) -> ComparisonSign
                          if me.value < you.value
                            return ME_SMALL
                          if me.value > you.value
                            return ME_LARGE
                          return SAME
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_MultipleOperatorProtocols_NoError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.__add__(you: Me) -> Me

                        protocol Subtractable
                          @readonly
                          routine Me.__sub__(you: Me) -> Me

                        record Complex obeys Addable, Subtractable
                          real: F64
                          imag: F64

                        @readonly
                        routine Complex.__add__(you: Complex) -> Complex
                          return Complex(real: me.real + you.real, imag: me.imag + you.imag)

                        @readonly
                        routine Complex.__sub__(you: Complex) -> Complex
                          return Complex(real: me.real - you.real, imag: me.imag - you.imag)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Missing Protocol Conformance

    [Fact]
    public void Analyze_AddWithoutAddable_ReportsError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.__add__(you: Me) -> Me

                        record Vector
                          x: S32
                          y: S32

                        @readonly
                        routine Vector.__add__(you: Vector) -> Vector
                          return Vector(x: me.x + you.x, y: me.y + you.y)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0,
            userMessage: "Expected error for missing Addable protocol");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "__add__",
                comparisonType: StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains(value: "Addable",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_EqWithoutEquatable_ReportsError()
    {
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.__eq__(you: Me) -> Bool

                        record Point
                          x: S32
                          y: S32

                        @readonly
                        routine Point.__eq__(you: Point) -> Bool
                          return me.x == you.x and me.y == you.y
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0,
            userMessage: "Expected error for missing Equatable protocol");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "__eq__",
                comparisonType: StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains(value: "Equatable",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_CmpWithoutComparable_ReportsError()
    {
        string source = """
                        choice ComparisonSign
                          ME_SMALL
                          SAME
                          ME_LARGE

                        protocol Comparable
                          @readonly
                          routine Me.__cmp__(you: Me) -> ComparisonSign

                        record Score
                          value: S32

                        @readonly
                        routine Score.__cmp__(you: Score) -> ComparisonSign
                          return SAME
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0,
            userMessage: "Expected error for missing Comparable protocol");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "__cmp__",
                comparisonType: StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains(value: "Comparable",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Non-Operator Methods (No Protocol Required)

    [Fact]
    public void Analyze_CreateWithoutProtocol_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine Point.__create__() -> Point
                          return Point(x: 0, y: 0)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_DestroyWithoutProtocol_NoError()
    {
        string source = """
                        entity Resource
                          handle: S32

                        routine Resource.__destroy__()
                          pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_RegularMethodWithoutProtocol_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        @readonly
                        routine Point.magnitude() -> F64
                          return 0.0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Suflae Tests

    [Fact]
    public void AnalyzeSuflae_AddableWithFollows_NoError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.__add__(you: Me) -> Me

                        entity Vector obeys Addable
                          x: Integer
                          y: Integer

                        @readonly
                        routine Vector.__add__(you: Vector) -> Vector
                          return Vector(x: me.x + you.x, y: me.y + you.y)
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void AnalyzeSuflae_AddWithoutAddable_ReportsError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.__add__(you: Me) -> Me

                        record Vector
                          x: Integer
                          y: Integer

                        @readonly
                        routine Vector.__add__(you: Vector) -> Vector
                          return Vector(x: me.x + you.x, y: me.y + you.y)
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.True(condition: result.Errors.Count > 0,
            userMessage: "Expected error for missing Addable protocol");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "__add__",
                comparisonType: StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains(value: "Addable",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}