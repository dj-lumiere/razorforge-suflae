using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the build-time shape check (RazorForge). While an `each` loop goes through a container, or a
/// statement works on one of its entity elements in place, nothing in that loop or statement may add to or
/// remove from the container, even through another routine: every routine carries the set of parameters
/// (and <c>me</c>) it may reshape, propagated through calls. Handing the container to a routine value, or
/// changing another shared handle of the same kind of container, is rejected because the build cannot
/// tell. Loop cases report RF-S625, element cases RF-S639.
/// </summary>
public class ShapeEffectTests
{
    private const string Prelude = """
                                   import IO/Console

                                   entity Box
                                     n: S64

                                   routine grow(xs: Modifying[List[S64]])
                                     xs.add_last(value: 99)
                                     return

                                   routine outer(ys: Modifying[List[S64]])
                                     grow(xs: ys)
                                     return

                                   routine total(xs: Viewing[List[S64]]) -> S64
                                     var sum = 0
                                     each x in xs
                                       sum = sum + x
                                     return sum

                                   """;

    [Fact]
    public void Analyze_LoopPassesItsListToAReshapingRoutine_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var nums = List[S64]()
                                    each x in nums
                                      grow(xs: nums.modify())
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "You are trying to pass 'nums' to 'grow'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReshapingDuringIteration);
    }

    [Fact]
    public void Analyze_ReshapeEffectPropagatesThroughCalls_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var nums = List[S64]()
                                    each x in nums
                                      outer(ys: nums.modify())
                                    return
                                  """;

        AssertHasErrorSa(source: source,
            expectedErrorSubstring: "You are trying to pass 'nums' to 'outer'");
    }

    [Fact]
    public void Analyze_UserMethodThatReshapesMe_Errors()
    {
        string source = Prelude + """
                                  routine List[S64].push_two()
                                    me.add_last(value: 1)
                                    me.add_last(value: 2)
                                    return

                                  routine start()
                                    var nums = List[S64]()
                                    each x in nums
                                      nums.push_two()
                                    return
                                  """;

        AssertHasErrorSa(source: source,
            expectedErrorSubstring: "You are trying to call 'nums.push_two()'");
    }

    [Fact]
    public void Analyze_LoopHandsItsListToARoutineValue_Errors()
    {
        string source = Prelude + """
                                  routine run_each(xs: Modifying[List[S64]], action: Routine[(Modifying[List[S64]]), None])
                                    each x in xs
                                      action(xs)
                                    return

                                  routine start()
                                    return
                                  """;

        AssertHasErrorSa(source: source,
            expectedErrorSubstring: "the builder cannot tell whether that routine adds to or removes from it");
    }

    [Fact]
    public void Analyze_ChangingAnotherSharedHandleOfTheSameKind_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var nums = List[S64]()
                                    var r = Retained[List[S64]](from: steal nums)
                                    var r2 = r.share()
                                    each x in r
                                      r2.add_last(value: 5)
                                    return
                                  """;

        AssertHasErrorSa(source: source, expectedErrorSubstring: "so they may be the same one");
    }

    [Fact]
    public void Analyze_ElementStatementHandsItsContainerToAReshapingRoutine_Errors()
    {
        string source = Prelude + """
                                  routine Box.absorb_all(all: Modifying[List[Box]])
                                    all.add_last(value: Box(n: 0))
                                    return

                                  routine start()
                                    var boxes = List[Box]()
                                    boxes.add_last(value: Box(n: 1))
                                    boxes[0].absorb_all(all: boxes.modify())
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "You are trying to pass 'boxes' to 'absorb_all'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TokenSourceReplaced);
    }

    [Fact]
    public void Analyze_ReadingOrChangingOtherThingsInTheLoop_Ok()
    {
        string source = Prelude + """
                                  routine start()
                                    var nums = List[S64]()
                                    nums.add_last(value: 1)
                                    var other = List[S64]()
                                    each x in nums
                                      discard total(xs: nums.view())
                                      nums[0] = 5
                                      grow(xs: other.modify())
                                    grow(xs: nums.modify())
                                    show(f"{nums.count()} {other.count()}")
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
}
