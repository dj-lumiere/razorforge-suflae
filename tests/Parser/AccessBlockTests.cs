using Xunit;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Tests for parsing scoped access blocks in RazorForge:
/// using x.view()/x.grasp() (single-threaded), using x.inspect!()/x.claim!() (multi-threaded),
/// using (resources).
/// </summary>
public class AccessBlockTests
{
    #region Viewing Block Tests (Single-threaded Read)
    /// <summary>
    /// Tests Parse_SimpleViewing.
    /// </summary>

    [Fact]
    public void Parse_SimpleViewing()
    {
        string source = """
                        routine test()
                          var data = SomeEntity()
                          using data.view() as v
                            show(v.value)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ViewingWithMultipleStatements.
    /// </summary>

    [Fact]
    public void Parse_ViewingWithMultipleStatements()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          using node.view() as v
                            var x = v.value
                            var y = v.name
                            process(x, y)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_NestedViewing.
    /// </summary>

    [Fact]
    public void Parse_NestedViewing()
    {
        string source = """
                        routine test()
                          var a = EntityA()
                          var b = EntityB()
                          using a.view() as va
                            using b.view() as vb
                              compare(va, vb)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ViewingWithMethodCall.
    /// </summary>

    [Fact]
    public void Parse_ViewingWithMethodCall()
    {
        string source = """
                        routine test()
                          var user = User()
                          using user.view() as v
                            show(v.name)
                            show(v.age)
                            show(v.get_full_name())
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Hijacking Block Tests (Single-threaded Exclusive)
    /// <summary>
    /// Tests Parse_SimpleHijacking.
    /// </summary>

    [Fact]
    public void Parse_SimpleHijacking()
    {
        string source = """
                        routine test()
                          var data = SomeEntity()
                          using data.grasp() as h
                            h.value = 42
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_HijackingWithMultipleMutations.
    /// </summary>

    [Fact]
    public void Parse_HijackingWithMultipleMutations()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          using node.grasp() as h
                            h.value = 42
                            h.name = "foo"
                            process(h)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_HijackingWithControlFlow.
    /// </summary>

    [Fact]
    public void Parse_HijackingWithControlFlow()
    {
        string source = """
                        routine test()
                          var counter = Counter()
                          using counter.grasp() as c
                            if c.value < 100
                              c.value += 1
                            else
                              c.reset()
                          return
                        """;

        AssertParses(source: source);
    }

    // Nested grasping test moved til Analyzer/MutabilityTests.cs
    // It parses correctly but should be rejected by semantic analysis (partial grasping)

    #endregion

    #region Inspecting Block Tests (Multi-threaded Read)
    /// <summary>
    /// Tests Parse_SimpleInspecting.
    /// </summary>

    [Fact]
    public void Parse_SimpleInspecting()
    {
        string source = """
                        routine test!()
                          var shared = data.share[MultiReadLock]()
                          using shared.inspect!() as r
                            show(r.value)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_InspectingMultipleReaders.
    /// </summary>

    [Fact]
    public void Parse_InspectingMultipleReaders()
    {
        string source = """
                        routine test!()
                          var shared = data.share[MultiReadLock]()
                          using shared.inspect!() as r1
                            using shared.inspect!() as r2
                              compare(r1, r2)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Seizing Block Tests (Multi-threaded Exclusive)
    /// <summary>
    /// Tests Parse_SimpleSeizing.
    /// </summary>

    [Fact]
    public void Parse_SimpleSeizing()
    {
        string source = """
                        routine test!()
                          var shared = data.share[Mutex]()
                          using shared.claim!() as w
                            w.value = 42
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_SeizingWithMultipleMutations.
    /// </summary>

    [Fact]
    public void Parse_SeizingWithMultipleMutations()
    {
        string source = """
                        routine test!()
                          var shared = counter.share[Mutex]()
                          using shared.claim!() as s
                            s.count += 1
                            s.last_updated = now()
                            s.notify_listeners()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_SeizingDowngradeToViewing.
    /// </summary>

    [Fact]
    public void Parse_SeizingDowngradeToViewing()
    {
        string source = """
                        routine test!()
                          var shared = data.share[MultiReadLock]()
                          using shared.claim!() as w
                            w.value = 42
                            using w.view() as v
                              show(v.value)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Using Block Tests (Resource Management)
    /// <summary>
    /// Tests Parse_SimpleUsing.
    /// </summary>

    [Fact]
    public void Parse_SimpleUsing()
    {
        string source = """
                        routine test!()
                          using open_file!("file.txt", mode: FileIO.Read) as file
                            var content = file.read_all()
                            process(content)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_UsingMultipleResources.
    /// </summary>

    [Fact]
    public void Parse_UsingMultipleResources()
    {
        string source = """
                        routine test!()
                          using open_file!("input.txt") as input, open_file!("output.txt", mode: FileIO.Write) as output
                            var data = input.read_all()
                            output.write(transform(data))
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_NestedUsing.
    /// </summary>

    [Fact]
    public void Parse_NestedUsing()
    {
        string source = """
                        routine test!()
                          using acquire_lock!() as lock
                            using open_connection!() as conn
                              process(conn)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_UsingWithControlFlow.
    /// </summary>

    [Fact]
    public void Parse_UsingWithControlFlow()
    {
        string source = """
                        routine test!()
                          using open_file!("data.txt") as file
                            for line in file.lines()
                              if line.starts_with("#")
                                continue
                              process_line(line)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_UsingWithErrorHandling.
    /// </summary>

    [Fact]
    public void Parse_UsingWithErrorHandling()
    {
        string source = """
                        routine process_files!(paths: List[Text])
                          for path in paths
                            using open_file!(path) as file
                              var content = file.read_all()
                              unless content.is_empty()
                                process(content)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Combined Access Patterns
    /// <summary>
    /// Tests Parse_ViewingThenHijacking.
    /// </summary>

    [Fact]
    public void Parse_ViewingThenHijacking()
    {
        string source = """
                        routine test()
                          var data = SomeEntity()
                          using data.view() as v
                            if v.needs_update()
                              pass
                          using data.grasp() as h
                            h.update()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_UsingWithViewing.
    /// </summary>

    [Fact]
    public void Parse_UsingWithViewing()
    {
        string source = """
                        routine test!()
                          var cache = Cache()
                          using open_file!("config.json") as file
                            var config = parse_json(file.read_all())
                            using cache.view() as c
                              apply_config(c, config)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ComplexAccessPattern.
    /// </summary>

    [Fact]
    public void Parse_ComplexAccessPattern()
    {
        string source = """
                        routine sync_data!()
                          var local = LocalData()
                          var shared = remote.share[MultiReadLock]()

                          using local.view() as l
                            using shared.claim!() as s
                              for item in l.items
                                s.add(item.clone())

                          using shared.inspect!() as r
                            verify!(r.count() > 0, "Sync failed")
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Inline Access Tests
    /// <summary>
    /// Tests Parse_InlineView.
    /// </summary>

    [Fact]
    public void Parse_InlineView()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          show(node.view().value)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_InlineHijack.
    /// </summary>

    [Fact]
    public void Parse_InlineHijack()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          node.grasp().value += 1
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_InlineViewAsArgument.
    /// </summary>

    [Fact]
    public void Parse_InlineViewAsArgument()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          process(node.view())
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_InlineMultipleViews.
    /// </summary>

    [Fact]
    public void Parse_InlineMultipleViews()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          compare(node.view(), node.view())
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Consume Operation Tests
    /// <summary>
    /// Tests Parse_ConsumeTransfer.
    /// </summary>

    [Fact]
    public void Parse_ConsumeTransfer()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          var owned = node.consume()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ConsumeAsArgument.
    /// </summary>

    [Fact]
    public void Parse_ConsumeAsArgument()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          take_ownership(node.consume())
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Share Operation Tests
    /// <summary>
    /// Tests Parse_ShareWithPolicy.
    /// </summary>

    [Fact]
    public void Parse_ShareWithPolicy()
    {
        string source = """
                        routine test()
                          var data = SomeEntity()
                          var shared = data.share[MultiReadLock]()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ShareMutex.
    /// </summary>

    [Fact]
    public void Parse_ShareMutex()
    {
        string source = """
                        routine test()
                          var data = SomeEntity()
                          var shared = data.share[Mutex]()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_TrackShared.
    /// </summary>

    [Fact]
    public void Parse_TrackShared()
    {
        string source = """
                        routine test()
                          var shared = data.share[Mutex]()
                          var weak = shared.track()
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion
}
