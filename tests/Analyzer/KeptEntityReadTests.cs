using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for RF-S413 on reads of an entity that something else goes on owning: a container element
/// (<c>boxes[0]</c>) or a field (<c>p.a</c>). Putting such a read anywhere that owns what it holds (a
/// variable, a field, a container slot, a consuming parameter, a returned value, a tuple) would make two
/// owners of one entity, and for an element the container's getitem would quietly duplicate it first.
/// Working on an element where it sits (a call or field access through it) stays legal.
/// </summary>
public class KeptEntityReadTests
{
    private const string Prelude = """
                                   import IO/Console

                                   entity Box
                                     n: S64

                                   entity Pair
                                     a: Box
                                     b: Box

                                   routine take(b: Box) -> S64
                                     return b.n

                                   """;

    /// <summary>Analyzes <paramref name="body"/> after the prelude, requiring an error containing <paramref name="expected"/>.</summary>
    private static IEnumerable<SemanticError> ErrorsFor(string body, string expected)
    {
        return AssertHasErrorSa(source: Prelude + body, expectedErrorSubstring: expected).Errors;
    }

    private static bool IsKeptEntityError(SemanticError e)
    {
        return e.Code == SemanticDiagnosticCode.BareEntityAssignment;
    }

    [Fact]
    public void Analyze_BindingAnEntityElement_Errors()
    {
        Assert.Contains(filter: IsKeptEntityError, collection: ErrorsFor(body: """
                                    routine start()
                                      var boxes = List[Box]()
                                      boxes.add_last(value: Box(n: 1))
                                      var b = boxes[0]
                                      return
                                    """,
            expected: "You are keeping in 'b' a 'Box' that its container still owns"));
    }

    [Fact]
    public void Analyze_ReturningAnEntityElement_Errors()
    {
        Assert.Contains(filter: IsKeptEntityError, collection: ErrorsFor(body: """
                                    routine first(boxes: Viewing[List[Box]]) -> Box
                                      return boxes[0]

                                    routine start()
                                      return
                                    """,
            expected: "You are returning a 'Box' that its container still owns"));
    }

    [Fact]
    public void Analyze_ReturningAFieldOfAViewedEntity_Errors()
    {
        Assert.Contains(filter: IsKeptEntityError, collection: ErrorsFor(body: """
                                    routine left(p: Viewing[Pair]) -> Box
                                      return p.a

                                    routine start()
                                      return
                                    """,
            expected: "You are returning a 'Box' that the value it belongs to still owns"));
    }

    [Fact]
    public void Analyze_PassingAnEntityElementToAConsumingParameter_Errors()
    {
        Assert.Contains(filter: IsKeptEntityError, collection: ErrorsFor(body: """
                                    routine start()
                                      var boxes = List[Box]()
                                      boxes.add_last(value: Box(n: 1))
                                      var holder = List[Box]()
                                      holder.add_last(value: boxes[0])
                                      discard take(b: boxes[0])
                                      return
                                    """,
            expected: "'add_last' takes ownership of its 'value'"));
    }

    [Fact]
    public void Analyze_PassingAnEntityElementToACreator_Errors()
    {
        Assert.Contains(filter: IsKeptEntityError, collection: ErrorsFor(body: """
                                    routine start()
                                      var boxes = List[Box]()
                                      boxes.add_last(value: Box(n: 1))
                                      var p = Pair(a: boxes[0], b: Box(n: 2))
                                      return
                                    """,
            expected: "'Pair' takes ownership of its 'a'"));
    }

    [Fact]
    public void Analyze_StoringAnEntityElement_Errors()
    {
        Assert.Contains(filter: IsKeptEntityError, collection: ErrorsFor(body: """
                                    routine start()
                                      var boxes = List[Box]()
                                      boxes.add_last(value: Box(n: 1))
                                      boxes.add_last(value: Box(n: 2))
                                      var p = Pair(a: Box(n: 3), b: Box(n: 4))
                                      p.b = boxes[1]
                                      return
                                    """,
            expected: "You are storing a 'Box' that its container still owns"));
    }

    [Fact]
    public void Analyze_PuttingAnEntityElementIntoATuple_Errors()
    {
        Assert.Contains(filter: IsKeptEntityError, collection: ErrorsFor(body: """
                                    routine start()
                                      var boxes = List[Box]()
                                      boxes.add_last(value: Box(n: 1))
                                      var t = (boxes[0], 1)
                                      return
                                    """,
            expected: "You are putting into a tuple a 'Box' that its container still owns"));
    }

    [Fact]
    public void Analyze_PuttingAVariableIntoATupleWithoutSteal_Errors()
    {
        Assert.Contains(filter: IsKeptEntityError, collection: ErrorsFor(body: """
                                    routine start()
                                      var z = Box(n: 1)
                                      var t = (z, 1)
                                      return
                                    """,
            expected: "You are putting into a tuple a 'Box' that 'z' still owns"));
    }

    [Fact]
    public void Analyze_CopyableElementMessageOffersDuplicate()
    {
        Assert.Contains(filter: IsKeptEntityError, collection: ErrorsFor(body: """
                                    routine start()
                                      var grid = List[List[S64]]()
                                      grid.add_last(value: List[S64]())
                                      var row = grid[0]
                                      return
                                    """,
            expected: "make an explicit copy with '.duplicate()'"));
    }

    [Fact]
    public void Analyze_WorkingOnElementsInPlaceOrTakingThemOut_Ok()
    {
        string source = Prelude + """
                                  routine make() -> Box
                                    var h = Box(n: 7)
                                    return h

                                  routine start()
                                    var boxes = List[Box]()
                                    boxes.add_last(value: Box(n: 1))
                                    boxes.add_last(value: Box(n: 2))
                                    boxes[0].n = 5
                                    var grid = List[List[S64]]()
                                    grid.add_last(value: List[S64]())
                                    grid[0].add_last(value: 3)
                                    var row = grid[0].duplicate()
                                    var moved = boxes.remove_at(index: 1)
                                    var fresh = make()
                                    var pair = (steal fresh, 1)
                                    show(f"{boxes[0].n} {row.count()} {moved.n} {pair.item1}")
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
}
