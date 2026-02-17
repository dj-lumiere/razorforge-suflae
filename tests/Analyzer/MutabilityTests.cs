using Compilers.Analysis.Results;
using RazorForge.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for mutability analysis in the semantic analyzer:
/// let vs var, mutation of immutable variables, readonly/writable methods.
/// </summary>
public class MutabilityTests
{
    #region Let Immutability

    [Fact]
    public void Analyze_LetReassignment_ReportsError()
    {
        string source = """
                        routine test() {
                            let x = 42
                            x = 10
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "immutable",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "reassign",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_LetCompoundAssignment_ReportsError()
    {
        string source = """
                        routine test() {
                            let x = 42
                            x += 10
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_VarReassignment_NoError()
    {
        string source = """
                        routine test() {
                            var x = 42
                            x = 10
                        }
                        """;

        Analyze(source: source);
        // Should be valid
    }

    [Fact]
    public void Analyze_VarCompoundAssignment_NoError()
    {
        string source = """
                        routine test() {
                            var x = 42
                            x += 10
                        }
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Entity Field Mutability

    [Fact]
    public void Analyze_VarFieldMutation_NoError()
    {
        string source = """
                        entity Counter {
                            count: S32
                        }

                        @writable
                        routine Counter.increment() {
                            me.count += 1
                        }
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_LetFieldMutation_ReportsError()
    {
        // All entity fields are immutable (no var/let distinction)
        // Mutating any field should produce an error
        string source = """
                        entity User {
                            id: U64
                            name: Text
                        }

                        @writable
                        routine User.set_id(new_id: U64) {
                            me.id = new_id
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Readonly vs Writable Methods

    [Fact]
    public void Analyze_ReadonlyMethodMutating_ReportsError()
    {
        string source = """
                        entity Counter {
                            count: S32
                        }

                        @readonly
                        routine Counter.increment() {
                            me.count += 1
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_ReadonlyMethodReading_NoError()
    {
        string source = """
                        entity Counter {
                            count: S32
                        }

                        @readonly
                        routine Counter.get_count() -> S32 {
                            return me.count
                        }
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_WritableMethodMutating_NoError()
    {
        string source = """
                        entity Counter {
                            count: S32
                        }

                        @writable
                        routine Counter.set_count(value: S32) {
                            me.count = value
                        }
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Record Field Mutability

    [Fact]
    public void Analyze_RecordFieldsImmutable_NoError()
    {
        string source = """
                        record Point {
                            x: F32
                            y: F32
                        }

                        routine test() {
                            let p = Point(x: 1.0, y: 2.0)
                            let x = p.x
                        }
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Parameter Mutability

    [Fact]
    public void Analyze_ParameterReassignment_ReportsError()
    {
        string source = """
                        routine test(x: S32) {
                            x = 10
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Suflae Mutability

    [Fact]
    public void AnalyzeSuflae_LetReassignment_ReportsError()
    {
        string source = """
                        routine test()
                            let x = 42
                            x = 10
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.True(condition: result.Errors.Count > 0);
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
                        routine test() {
                            var items = [1, 2, 3]
                            items[0] = 42
                        }
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_IndexAssignmentOnLet_ReportsError()
    {
        string source = """
                        routine test() {
                            let items = [1, 2, 3]
                            items[0] = 42
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Hijacking Restrictions

    [Fact]
    public void Analyze_NestedHijacking_ReportsError()
    {
        // Nested hijacking (partial hijacking) should not be allowed
        // You cannot hijack a child of an already-hijacked object
        string source = """
                        entity Child {
                            value: S32
                        }

                        entity Parent {
                            child: Child
                        }

                        routine test() {
                            let parent = Parent(child: Child(value: 0))
                            hijacking parent as p {
                                hijacking p.child as c {
                                    c.value = 10
                                }
                            }
                        }
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
                        entity Document {
                            title: Text
                        }

                        routine test() {
                            let doc1 = Document(title: "My Doc")
                            let doc2 = doc1
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }

    [Fact]
    public void Analyze_EntityConstructorAssignment_NoError()
    {
        string source = """
                        entity Document {
                            title: Text
                        }

                        routine test() {
                            let doc1 = Document(title: "My Doc")
                            let doc2 = Document(title: "Other")
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }

    [Fact]
    public void Analyze_RecordBareAssignment_NoError()
    {
        string source = """
                        record Point {
                            x: S32
                            y: S32
                        }

                        routine test() {
                            let p1 = Point(x: 1, y: 2)
                            let p2 = p1
                        }
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
                            let doc1 = Document(title: "My Doc")
                            let doc2 = doc1
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }

    #endregion
}
