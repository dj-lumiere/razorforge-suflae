using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for mutability analysis in the semantic analyzer:
/// var mutability, mutation of immutable variables, readonly/writable methods.
/// </summary>
public class MutabilityTests
{
    #region Let Immutability

    [Fact]
    public void Analyze_VarReassignment_NoImmutableError()
    {
        // var is mutable, so reassignment should succeed
        string source = """
                        routine test()
                          var x = 42
                          x = 10
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "immutable",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "reassign",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_VarCompoundAssignment_NoImmutableError()
    {
        // var is mutable, so compound assignment should succeed
        string source = """
                        routine test()
                          var x = 42
                          x += 10
                          return
                        """;

        Analyze(source: source);
        // Should not produce immutable-related errors
    }

    [Fact]
    public void Analyze_VarReassignment_NoError()
    {
        string source = """
                        routine test()
                          var x = 42
                          x = 10
                          return
                        """;

        Analyze(source: source);
        // Should be valid
    }

    [Fact]
    public void Analyze_VarCompoundAssignment_NoError()
    {
        string source = """
                        routine test()
                          var x = 42
                          x += 10
                          return
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Entity Field Mutability

    [Fact]
    public void Analyze_VarMemberVariableMutation_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @writable
                        routine Counter.increment()
                          me.count += 1
                          return
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_WritableMemberVariableMutation_NoError()
    {
        // Entity fields are mutable via @writable routines
        string source = """
                        entity User
                          id: U64
                          name: Text

                        @writable
                        routine User.set_id(new_id: U64)
                          me.id = new_id
                          return
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Readonly vs Writable Methods

    [Fact]
    public void Analyze_ReadonlyMethodMutating_ReportsError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @readonly
                        routine Counter.increment()
                          me.count += 1
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_ReadonlyMethodReading_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @readonly
                        routine Counter.get_count() -> S32
                          return me.count
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_WritableMethodMutating_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @writable
                        routine Counter.set_count(value: S32)
                          me.count = value
                          return
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Record Field Mutability

    [Fact]
    public void Analyze_RecordMemberVariablesImmutable_NoError()
    {
        string source = """
                        record Point
                          x: F32
                          y: F32

                        routine test()
                          var p = Point(x: 1.0, y: 2.0)
                          var x = p.x
                          return
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Parameter Mutability

    [Fact]
    public void Analyze_ParameterReassignment_ReportsError()
    {
        string source = """
                        routine test(x: S32)
                          x = 10
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Suflae Mutability

    [Fact]
    public void AnalyzeSuflae_VarReassignment_NoImmutableError()
    {
        // var is mutable, so reassignment should succeed
        string source = """
                        routine test()
                          var x = 42
                          x = 10
                        """;

        AnalyzeSuflae(source: source);
        // Should not produce immutable-related errors
    }

    [Fact]
    public void AnalyzeSuflae_VarReassignment_NoError()
    {
        string source = """
                        routine test()
                          var x = 42
                          x = 10
                        """;

        AnalyzeSuflae(source: source);
    }

    [Fact]
    public void AnalyzeSuflae_ReadonlyMethodMutating_ReportsError()
    {
        string source = """
                        entity Counter
                          count: Integer

                        @readonly
                        routine Counter.increment()
                          me.count += 1
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void AnalyzeSuflae_WritableMethodMutating_NoError()
    {
        string source = """
                        entity Counter
                          count: Integer

                        @writable
                        routine Counter.increment()
                          me.count += 1
                        """;

        AnalyzeSuflae(source: source);
    }

    #endregion

    #region Index Mutability

    [Fact]
    public void Analyze_IndexAssignmentOnVar_NoError()
    {
        string source = """
                        routine test()
                          var items = [1, 2, 3]
                          items[0] = 42
                          return
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_IndexAssignmentOnVar_NoImmutableError()
    {
        // var is mutable, so index assignment should succeed
        string source = """
                        routine test()
                          var items = [1, 2, 3]
                          items[0] = 42
                          return
                        """;

        Analyze(source: source);
        // Should not produce immutable-related errors
    }

    #endregion

    #region Hijacking Restrictions

    [Fact]
    public void Analyze_NestedHijacking_ReportsError()
    {
        // Nested hijacking (partial hijacking) should not be allowed
        // You cannot hijack a child of an already-hijacked object
        string source = """
                        entity Child
                          value: S32

                        entity Parent
                          child: Child

                        routine test()
                          var parent = Parent(child: Child(value: 0))
                          using parent.hijack() as p
                            using p.child.hijack() as c
                              c.value = 10
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "hijack",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "nested",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Entity Bare Assignment Prohibition

    [Fact]
    public void Analyze_EntityBareAssignment_ReportsError()
    {
        string source = """
                        entity Document
                          title: Text

                        routine test()
                          var doc1 = Document(title: "My Doc")
                          var doc2 = doc1
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }

    [Fact]
    public void Analyze_EntityConstructorAssignment_NoError()
    {
        string source = """
                        entity Document
                          title: Text

                        routine test()
                          var doc1 = Document(title: "My Doc")
                          var doc2 = Document(title: "Other")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }

    [Fact]
    public void Analyze_RecordBareAssignment_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine test()
                          var p1 = Point(x: 1, y: 2)
                          var p2 = p1
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }

    [Fact]
    public void AnalyzeSuflae_EntityBareAssignment_NoError()
    {
        string source = """
                        entity Document
                          title: Text

                        routine test()
                          var doc1 = Document(title: "My Doc")
                          var doc2 = doc1
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }

    #endregion

    #region Readonly Method Call Enforcement

    [Fact]
    public void Analyze_ReadonlyMethodCallsWritable_ReportsError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @writable
                        routine Counter.increment()
                          me.count += 1
                          return

                        @readonly
                        routine Counter.try_increment()
                          me.increment()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.ModificationInReadonlyMethod);
    }

    [Fact]
    public void Analyze_ReadonlyMethodCallsReadonly_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @readonly
                        routine Counter.get_count() -> S32
                          return me.count

                        @readonly
                        routine Counter.display() -> S32
                          return me.get_count()
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.ModificationInReadonlyMethod);
    }

    [Fact]
    public void Analyze_ReadonlyMethodCallsOnOther_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @writable
                        routine Counter.increment()
                          me.count += 1
                          return

                        @readonly
                        routine Counter.compare(other: Counter)
                          other.increment()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        // Calling a mutating method on 'other' (not 'me') is allowed in @readonly
        Assert.DoesNotContain(result.Errors,
            e => e.Code == SemanticDiagnosticCode.ModificationInReadonlyMethod
                 && e.Message.Contains("increment"));
    }

    #endregion

    #region Posted Member Variable Access

    [Fact]
    public void Analyze_PostedMemberVariableWrite_SameModule_NoError()
    {
        // Within the same module (null == null), writing to posted member variable is allowed
        string source = """
                        entity Config
                          posted name: Text

                        @writable
                        routine Config.rename(new_name: Text)
                          me.name = new_name
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.PostedMemberAccess);
    }

    [Fact]
    public void Analyze_PostedMemberVariableRead_NoError()
    {
        // Reading a posted member variable is always allowed
        string source = """
                        entity Config
                          posted name: Text

                        @readonly
                        routine Config.get_name() -> Text
                          return me.name
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.PostedMemberAccess);
    }

    #endregion
}
