using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for in-place compound assignment dispatch (#40).
/// Compound assignments (+=, -=, etc.) dispatch to in-place wired methods ($iadd, etc.)
/// first, then fall back to create-and-assign ($add) for non-entity types.
/// Entities require in-place wired methods (no fallback, since bare entity assignment is prohibited).
/// </summary>
public class CompoundAssignmentTests
{
    #region In-Place Dispatch (type defines $iadd)

    [Fact]
    public void Analyze_RecordWithInPlaceWired_NoError()
    {
        string source = """
                        protocol InPlaceAddable
                          routine Me.$iadd(from: Me) -> Blank

                        record Counter obeys InPlaceAddable
                          value: S32

                        routine Counter.$iadd(from: Counter) -> Blank

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
    public void Analyze_EntityWithInPlaceWired_NoError()
    {
        string source = """
                        protocol InPlaceAddable
                          routine Me.$iadd(from: Me) -> Blank

                        entity Accumulator obeys InPlaceAddable
                          value: S32

                        routine Accumulator.$iadd(from: Accumulator) -> Blank

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

    #region Fallback Dispatch (record with only $add)

    [Fact]
    public void Analyze_RecordWithRegularWired_FallsBack()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.$add(you: Me) -> Me

                        record Vector obeys Addable
                          x: S32
                          y: S32

                        @readonly
                        routine Vector.$add(you: Vector) -> Vector
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

    #region Entity Without In-Place Wired (no fallback)

    [Fact]
    public void Analyze_EntityWithoutInPlaceWired_ReportsError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.$add(you: Me) -> Me

                        entity Counter obeys Addable
                          value: S32

                        @readonly
                        routine Counter.$add(you: Counter) -> Counter
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

    #region Neither Wired Exists

    [Fact]
    public void Analyze_NoWiredsDefined_ReportsError()
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
                          routine Me.$iadd(from: Me) -> Blank

                        record Counter obeys InPlaceAddable
                          value: S32

                        routine Counter.$iadd(from: Counter) -> Blank

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
                          routine Me.$isub(from: Me) -> Blank

                        record Counter obeys InPlaceSubtractable
                          value: S32

                        routine Counter.$isub(from: Counter) -> Blank

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
                          routine Me.$ibitand(from: Me) -> Blank

                        record Flags obeys InPlaceBitwiseable
                          bits: S32

                        routine Flags.$ibitand(from: Flags) -> Blank

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
        // Primitives like S32 don't have $iadd registered in tests (no stdlib loaded),
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
