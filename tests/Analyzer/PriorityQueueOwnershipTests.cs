using Compiler.Diagnostics;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Type-signature tests for the entity-in-carrier ownership rule applied to PriorityQueue.
/// `PriorityQueue[K, V]` is an entity, so a var binding must wrap it in `Owned[…]`.
/// `Text` is also an entity, so when stored as the PQ's value type it must itself be
/// `Text`. Only the fully-wrapped form is legal.
/// </summary>
public class PriorityQueueOwnershipTests
{
    /// <summary>
    /// `var pq: PriorityQueue[S128, Text] = {…}` — bare PriorityQueue entity in a var
    /// binding violates the entity-ownership rule. The carrier itself must be Owned-wrapped.
    /// </summary>
    [Fact]
    public void PriorityQueueLiteral_BarePQInVarBinding_ReportsError()
    {
        string source = """
                        routine test()
                          var pq: PriorityQueue[S128, Text] = {1: "high", 10: "low", 5: "mid"}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotEmpty(collection: result.Errors);
    }

    /// <summary>
    /// `var pq: Owned[PriorityQueue[S128, Text]] = {…}` — bare Text entity as the PQ value
    /// type violates the bare-entity-in-carrier rule. Each entity stored inside the PQ must
    /// be wrapped (Owned/Retained/Tracked).
    /// </summary>
    [Fact]
    public void PriorityQueueLiteral_BareTextValueType_ReportsError()
    {
        string source = """
                        routine test()
                          var pq: Owned[PriorityQueue[S128, Text]] = {1: "high", 10: "low", 5: "mid"}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotEmpty(collection: result.Errors);
    }

    /// <summary>
    /// `var pq: Owned[PriorityQueue[S128, Text]] = {…}` — fully-wrapped form: the
    /// PriorityQueue is Owned, and the inner Text value is Owned. This is the only legal
    /// shape for a PriorityQueue of entity values.
    /// </summary>
    [Fact]
    public void PriorityQueueLiteral_FullyWrapped_NoError()
    {
        string source = """
                        routine test()
                          var pq: Owned[PriorityQueue[S128, Text]] = {1: "high", 10: "low", 5: "mid"}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
}
