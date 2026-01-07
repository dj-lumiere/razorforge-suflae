using Xunit;

namespace RazorForge.Tests.Parser;

using Compilers.Shared.AST;
using static TestHelpers;

/// <summary>
/// Tests for parsing scoped access blocks in RazorForge:
/// viewing/hijacking (single-threaded), inspecting/seizing (multi-threaded), using (resources).
/// </summary>
public class AccessBlockTests
{
    #region Viewing Block Tests (Single-threaded Read)

    [Fact]
    public void Parse_SimpleViewing()
    {
        string source = """
                        routine test() {
                            let data = SomeEntity()
                            viewing data as v {
                                show(v.value)
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_ViewingWithMultipleStatements()
    {
        string source = """
                        routine test() {
                            let node = Node(42)
                            viewing node as v {
                                let x = v.value
                                let y = v.name
                                process(x, y)
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_NestedViewing()
    {
        string source = """
                        routine test() {
                            let a = EntityA()
                            let b = EntityB()
                            viewing a as va {
                                viewing b as vb {
                                    compare(va, vb)
                                }
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_ViewingWithMethodCall()
    {
        string source = """
                        routine test() {
                            let user = User()
                            viewing user as v {
                                show(v.name)
                                show(v.age)
                                show(v.get_full_name())
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Hijacking Block Tests (Single-threaded Exclusive)

    [Fact]
    public void Parse_SimpleHijacking()
    {
        string source = """
                        routine test() {
                            let data = SomeEntity()
                            hijacking data as h {
                                h.value = 42
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_HijackingWithMultipleMutations()
    {
        string source = """
                        routine test() {
                            let node = Node(42)
                            hijacking node as h {
                                h.value = 42
                                h.name = "foo"
                                process(h)
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_HijackingWithControlFlow()
    {
        string source = """
                        routine test() {
                            let counter = Counter()
                            hijacking counter as c {
                                if c.value < 100 {
                                    c.value += 1
                                } else {
                                    c.reset()
                                }
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    // Nested hijacking test moved to Analyzer/MutabilityTests.cs
    // It parses correctly but should be rejected by semantic analysis (partial hijacking)

    #endregion

    #region Inspecting Block Tests (Multi-threaded Read)

    [Fact]
    public void Parse_SimpleInspecting()
    {
        string source = """
                        routine test() {
                            let shared = data.share<MultiReadLock>()
                            inspecting shared as r {
                                show(r.value)
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_InspectingMultipleReaders()
    {
        string source = """
                        routine test() {
                            let shared = data.share<MultiReadLock>()
                            inspecting shared as r1 {
                                inspecting shared as r2 {
                                    compare(r1, r2)
                                }
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Seizing Block Tests (Multi-threaded Exclusive)

    [Fact]
    public void Parse_SimpleSeizing()
    {
        string source = """
                        routine test() {
                            let shared = data.share<Mutex>()
                            seizing shared as w {
                                w.value = 42
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_SeizingWithMultipleMutations()
    {
        string source = """
                        routine test() {
                            let shared = counter.share<Mutex>()
                            seizing shared as s {
                                s.count += 1
                                s.last_updated = now()
                                s.notify_listeners()
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_SeizingDowngradeToViewing()
    {
        string source = """
                        routine test() {
                            let shared = data.share<MultiReadLock>()
                            seizing shared as w {
                                w.value = 42
                                viewing w as v {
                                    show(v.value)
                                }
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Using Block Tests (Resource Management)

    [Fact]
    public void Parse_SimpleUsing()
    {
        string source = """
                        routine test!() {
                            using open!("file.txt", mode: FileIO.Read) as file {
                                let content = file.read_all()
                                process(content)
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_UsingMultipleResources()
    {
        string source = """
                        routine test!() {
                            using open!("input.txt") as input, open!("output.txt", mode: FileIO.Write) as output {
                                let data = input.read_all()
                                output.write(transform(data))
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_NestedUsing()
    {
        string source = """
                        routine test!() {
                            using acquire_lock!() as lock {
                                using open_connection!() as conn {
                                    process(conn)
                                }
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_UsingWithControlFlow()
    {
        string source = """
                        routine test!() {
                            using open!("data.txt") as file {
                                for line in file.lines() {
                                    if line.starts_with("#") {
                                        continue
                                    }
                                    process_line(line)
                                }
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_UsingWithErrorHandling()
    {
        string source = """
                        routine process_files!(paths: List<Text>) {
                            for path in paths {
                                using open!(path) as file {
                                    let content = file.read_all()
                                    unless content.is_empty() {
                                        process(content)
                                    }
                                }
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Combined Access Patterns

    [Fact]
    public void Parse_ViewingThenHijacking()
    {
        string source = """
                        routine test() {
                            let data = SomeEntity()
                            viewing data as v {
                                if v.needs_update() {
                                    pass
                                }
                            }
                            hijacking data as h {
                                h.update()
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_UsingWithViewing()
    {
        string source = """
                        routine test!() {
                            let cache = Cache()
                            using open!("config.json") as file {
                                let config = parse_json(file.read_all())
                                viewing cache as c {
                                    apply_config(c, config)
                                }
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_ComplexAccessPattern()
    {
        string source = """
                        routine sync_data!() {
                            let local = LocalData()
                            let shared = remote.share<MultiReadLock>()

                            viewing local as l {
                                seizing shared as s {
                                    for item in l.items {
                                        s.add(item.clone())
                                    }
                                }
                            }

                            inspecting shared as r {
                                verify!(r.count() > 0, "Sync failed")
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Inline Access Tests

    [Fact]
    public void Parse_InlineView()
    {
        string source = """
                        routine test() {
                            let node = Node(42)
                            show(node.view().value)
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_InlineHijack()
    {
        string source = """
                        routine test() {
                            let node = Node(42)
                            node.hijack().value += 1
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_InlineViewAsArgument()
    {
        string source = """
                        routine test() {
                            let node = Node(42)
                            process(node.view())
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_InlineMultipleViews()
    {
        string source = """
                        routine test() {
                            let node = Node(42)
                            compare(node.view(), node.view())
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Consume Operation Tests

    [Fact]
    public void Parse_ConsumeTransfer()
    {
        string source = """
                        routine test() {
                            let node = Node(42)
                            let owned = node.consume()
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_ConsumeAsArgument()
    {
        string source = """
                        routine test() {
                            let node = Node(42)
                            take_ownership(node.consume())
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Share Operation Tests

    [Fact]
    public void Parse_ShareWithPolicy()
    {
        string source = """
                        routine test() {
                            let data = SomeEntity()
                            let shared = data.share<MultiReadLock>()
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_ShareMutex()
    {
        string source = """
                        routine test() {
                            let data = SomeEntity()
                            let shared = data.share<Mutex>()
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_TrackShared()
    {
        string source = """
                        routine test() {
                            let shared = data.share<Mutex>()
                            let weak = shared.track()
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion
}
