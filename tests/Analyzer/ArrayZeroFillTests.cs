using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// RF-S641: <c>Array[T, N]()</c> fills every slot with zero, which is only a real value when T has one.
/// An entity, a reference-counted handle, or a record holding one would get null slots that crash when
/// read or torn down, so those arrays are built from their elements (<c>Array[N](a, b, ...)</c>).
/// Also RF-S150 on a generic construction whose type argument misses the type's <c>needs</c>.
/// </summary>
public class ArrayZeroFillTests
{
    private const string Prelude = """
                                   import Collections

                                   entity Node
                                     n: S64

                                   record Slot
                                     node: Retained[Node]

                                   """;

    [Fact]
    public void Analyze_ZeroFilledArrayOfRcHandleRecord_Errors()
    {
        AnalysisResult result = AssertHasErrorSa(source: Prelude + """
                                                          routine start()
                                                            var slots = Array[Slot, 2]()
                                                            return
                                                          """,
            expectedErrorSubstring: "'Slot' has no zero value (its 'node'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArrayElementHasNoZeroValue);
    }

    [Fact]
    public void Analyze_ZeroFilledArrayOfEntities_Errors()
    {
        AssertHasErrorSa(source: Prelude + """
                                           routine start()
                                             var nodes = Array[Node, 2]()
                                             return
                                           """,
            expectedErrorSubstring: "'Node' is an entity");
    }

    [Fact]
    public void Analyze_ZeroFilledArrayOfZeroableTypes_AndElementListArray_Pass()
    {
        AnalysisResult result = AnalyzeSa(source: Prelude + """
                                                     routine start()
                                                       var nums = Array[S64, 4]()
                                                       var texts = Array[Text, 2]()
                                                       var slots = Array[2](Slot(node: Retained[Node](from: Node(n: 1))), Slot(node: Retained[Node](from: Node(n: 2))))
                                                       return
                                                     """);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_SplitListOfEntity_ViolatesSplittable()
    {
        AssertHasErrorSa(source: Prelude + """
                                           routine start()
                                             var sl = SplitList[List[S64]]()
                                             return
                                           """,
            expectedErrorSubstring: "does not implement protocol 'Splittable'");
    }
}
