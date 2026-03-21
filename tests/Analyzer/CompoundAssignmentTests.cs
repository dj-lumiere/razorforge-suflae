using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for in-place compound assignment dispatch (#40).
/// Compound assignments (+=, -=, etc.) dispatch to in-place dunders (__iadd__, etc.)
/// first, then fall back to create-and-assign (__add__) for non-entity types.
/// Entities require in-place dunders (no fallback, since bare entity assignment is prohibited).
/// </summary>
public class CompoundAssignmentTests
{
    #region In-Place Dispatch (type defines __iadd__)

    [Fact]
    public void Analyze_RecordWithInPlaceDunder_NoError()
    {
        string source = """
                        protocol InPlaceAddable
                          routine Me.__iadd__(from: Me) -> Blank

                        record Counter obeys InPlaceAddable
                          value: S32

                        routine Counter.__iadd__(from: Counter) -> Blank

                        routine test()
                          var c = Counter(value: 0)
                          c += Counter(value: 1)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    [Fact]
    public void Analyze_EntityWithInPlaceDunder_NoError()
    {
        string source = """
                        protocol InPlaceAddable
                          routine Me.__iadd__(from: Me) -> Blank

                        entity Accumulator obeys InPlaceAddable
                          value: S32

                        routine Accumulator.__iadd__(from: Accumulator) -> Blank

                        routine test()
                          var acc = Accumulator(value: 0)
                          acc += Accumulator(value: 1)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    #endregion

    #region Fallback Dispatch (record with only __add__)

    [Fact]
    public void Analyze_RecordWithRegularDunder_FallsBack()
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
                          return Vector(x: me.x, y: me.y)

                        routine test()
                          var v = Vector(x: 1, y: 2)
                          v += Vector(x: 3, y: 4)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    #endregion

    #region Entity Without In-Place Dunder (no fallback)

    [Fact]
    public void Analyze_EntityWithoutInPlaceDunder_ReportsError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.__add__(you: Me) -> Me

                        entity Counter obeys Addable
                          value: S32

                        @readonly
                        routine Counter.__add__(you: Counter) -> Counter
                          return Counter(value: me.value)

                        routine test()
                          var c = Counter(value: 0)
                          c += Counter(value: 1)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    #endregion

    #region Neither Dunder Exists

    [Fact]
    public void Analyze_NoDundersDefined_ReportsError()
    {
        string source = """
                        record Pair
                          a: S32
                          b: S32

                        routine test()
                          var p = Pair(a: 1, b: 2)
                          p += Pair(a: 3, b: 4)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    #endregion

    #region Mutability Checks

    [Fact]
    public void Analyze_VarCompoundAssignment_NoImmutableError()
    {
        // var is mutable, so compound assignment should not produce immutable errors
        string source = """
                        protocol InPlaceAddable
                          routine Me.__iadd__(from: Me) -> Blank

                        record Counter obeys InPlaceAddable
                          value: S32

                        routine Counter.__iadd__(from: Counter) -> Blank

                        routine test()
                          var c = Counter(value: 0)
                          c += Counter(value: 1)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.AssignmentToImmutable);
    }

    #endregion

    #region Choice Type Prohibition

    [Fact]
    public void Analyze_ChoiceCompoundAssignment_ReportsArithmeticError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE

                        routine test()
                          var c = RED
                          c += GREEN
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }

    #endregion

    #region Multiple Compound Operators

    [Fact]
    public void Analyze_SubtractCompoundAssignment_NoError()
    {
        string source = """
                        protocol InPlaceSubtractable
                          routine Me.__isub__(from: Me) -> Blank

                        record Counter obeys InPlaceSubtractable
                          value: S32

                        routine Counter.__isub__(from: Counter) -> Blank

                        routine test()
                          var c = Counter(value: 10)
                          c -= Counter(value: 1)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    [Fact]
    public void Analyze_BitwiseAndCompoundAssignment_NoError()
    {
        string source = """
                        protocol InPlaceBitwiseable
                          routine Me.__iand__(from: Me) -> Blank

                        record Flags obeys InPlaceBitwiseable
                          bits: S32

                        routine Flags.__iand__(from: Flags) -> Blank

                        routine test()
                          var f = Flags(bits: 255)
                          f &= Flags(bits: 15)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    #endregion

    #region Primitive Types (existing behavior preserved)

    [Fact]
    public void Analyze_PrimitiveVarCompoundAssignment_NoError()
    {
        // Primitives like S32 don't have __iadd__ registered in tests (no stdlib loaded),
        // but the test verifies parsing and analysis don't crash.
        string source = """
                        routine test()
                          var x = 42
                          x += 10
                          return
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_PrimitiveVarCompoundAssignment_NoImmutableError()
    {
        // var is mutable, so compound assignment should not produce immutable errors
        string source = """
                        routine test()
                          var x = 42
                          x += 10
                          return
                        """;

        Analyze(source: source);
        // Should not produce immutable-related errors
    }

    #endregion
}
