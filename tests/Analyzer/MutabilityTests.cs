using Compilers.Analysis.Results;
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

        AnalysisResult result = Analyze(source: source);
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

        AnalysisResult result = Analyze(source: source);
    }

    #endregion

    #region Entity Field Mutability

    [Fact]
    public void Analyze_VarFieldMutation_NoError()
    {
        string source = """
                        entity Counter {
                            var count: S32
                        }

                        @writable
                        routine Counter.increment() {
                            me.count += 1
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
    }

    [Fact]
    public void Analyze_LetFieldMutation_ReportsError()
    {
        string source = """
                        entity User {
                            let id: U64
                            var name: Text
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
                            var count: S32
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
                            var count: S32
                        }

                        @readonly
                        routine Counter.get_count() -> S32 {
                            return me.count
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
    }

    [Fact]
    public void Analyze_WritableMethodMutating_NoError()
    {
        string source = """
                        entity Counter {
                            var count: S32
                        }

                        @writable
                        routine Counter.set_count(value: S32) {
                            me.count = value
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
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

        AnalysisResult result = Analyze(source: source);
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
                        routine test():
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
                        routine test():
                            var x = 42
                            x = 10
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
    }

    [Fact]
    public void AnalyzeSuflae_ReadonlyMethodMutating_ReportsError()
    {
        string source = """
                        entity Counter:
                            var count: Integer

                        @readonly
                        routine Counter.increment():
                            me.count += 1
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void AnalyzeSuflae_WritableMethodMutating_NoError()
    {
        string source = """
                        entity Counter:
                            var count: Integer

                        @writable
                        routine Counter.increment():
                            me.count += 1
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
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

        AnalysisResult result = Analyze(source: source);
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

    [Fact(Skip = "Requires namespace Core auto-import and nested hijacking validation")]
    public void Analyze_NestedHijacking_ReportsError()
    {
        // Nested hijacking (partial hijacking) should not be allowed
        // You cannot hijack a child of an already-hijacked object
        string source = """
                        entity Child {
                            var value: S32
                        }

                        entity Parent {
                            var child: Child
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
}
