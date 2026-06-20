using Compiler.Diagnostics;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the threaded-routine argument boundary (RF-S632). A `threaded routine` spawns an OS
/// thread, so every parameter must cross the boundary safely: trivially-copyable value data is
/// passed BY VALUE (an independent copy); a thread-shareable wrapper (Atomic/Shared/Watched) is
/// passed BY REFERENCE (carries its own synchronization). Anything else — a bare entity, or a
/// record/tuple that transitively owns a single-threaded RC wrapper or a scoped token — would
/// silently alias unsynchronized state across threads and is rejected.
/// </summary>
public class ThreadArgShareabilityTests
{
    private const string Prelude = """
                                   import IO/Console

                                   record Point
                                     posted x: S64
                                     posted y: S64

                                   entity Node
                                     value: S64

                                   """;

    [Fact]
    public void Analyze_ScalarThreadArg_Ok()
    {
        string source = Prelude + """
                                  threaded routine work(n: S64) -> S64
                                    return n + 1

                                  routine start()
                                    var t = work(n: 5)
                                    discard t.retrieve!()
                                    return
                                  """;

        AssertAnalyzesSa(source: source);
    }

    [Fact]
    public void Analyze_ValueRecordThreadArg_Ok()
    {
        // A plain value record (no interior reference) is copied by value — safe to cross.
        string source = Prelude + """
                                  threaded routine work(p: Point) -> S64
                                    return p.x + p.y

                                  routine start()
                                    var origin = Point(x: 1, y: 2)
                                    var t = work(p: origin)
                                    discard t.retrieve!()
                                    return
                                  """;

        AssertAnalyzesSa(source: source);
    }

    [Fact]
    public void Analyze_AtomicThreadArg_Ok()
    {
        // Atomic carries its own synchronization — shared by reference across the boundary.
        string source = Prelude + """
                                  threaded routine work(cell: Atomic[S64]) -> S64
                                    discard cell.fetch_add(delta: 1_s64)
                                    return 0_s64

                                  routine start()
                                    var counter = Atomic[S64](initial: 0_s64)
                                    var t = work(cell: counter)
                                    discard t.retrieve!()
                                    return
                                  """;

        AssertAnalyzesSa(source: source);
    }

    [Fact]
    public void Analyze_SharedThreadArg_Ok()
    {
        // Shared is an atomic Arc — explicitly shareable across threads.
        string source = Prelude + """
                                  threaded routine work(s: Shared[Node, ReadOnly]) -> S64
                                    return 0_s64

                                  routine start()
                                    var s = Node(value: 1).share[ReadOnly]()
                                    var t = work(s: s.share())
                                    discard t.retrieve!()
                                    return
                                  """;

        AssertAnalyzesSa(source: source);
    }

    [Fact]
    public void Analyze_BareEntityThreadArg_Errors()
    {
        // A bare entity is a heap handle — passing it would alias the same object across threads.
        string source = Prelude + """
                                  threaded routine work(node: Node) -> S64
                                    return node.value

                                  routine start()
                                    var n = Node(value: 1)
                                    var t = work(node: n)
                                    discard t.retrieve!()
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "cannot cross the thread boundary safely");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThreadArgNotShareable);
    }

    [Fact]
    public void Analyze_RecordOwningRetainedThreadArg_Errors()
    {
        // A record that transitively owns a single-threaded RC wrapper (Retained) would alias its
        // interior across threads — must use Shared instead.
        string source = Prelude + """
                                  record Holder
                                    posted node: Retained[Node]

                                  threaded routine work(h: Holder) -> S64
                                    return 0_s64

                                  routine start()
                                    var h = Holder(node: Node(value: 1).retain())
                                    var t = work(h: h)
                                    discard t.retrieve!()
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "transitively owns `Retained`");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThreadArgNotShareable);
    }
}
