using Xunit;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Tests for parsing resource management blocks in Suflae.
/// Suflae uses automatic memory management (GC-assisted), so it only has 'using' for resources.
/// Unlike RazorForge, Suflae does not have viewing/hijacking/inspecting/seizing.
/// </summary>
public class SuflaeAccessBlockTests
{
    #region Using Block Tests (Resource Management)
    /// <summary>
    /// Tests ParseSuflae_SimpleUsing.
    /// </summary>

    [Fact]
    public void ParseSuflae_SimpleUsing()
    {
        string source = """
                        routine test()
                          using open_file("file.txt") as file
                            var content = file.read_all()
                            process(content)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_UsingMultipleResources.
    /// </summary>

    [Fact]
    public void ParseSuflae_UsingMultipleResources()
    {
        string source = """
                        routine test()
                          using open_file("input.txt") as input, open_file("output.txt", mode: Write) as output
                            var data = input.read_all()
                            output.write(transform(data))
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_NestedUsing.
    /// </summary>

    [Fact]
    public void ParseSuflae_NestedUsing()
    {
        string source = """
                        routine test()
                          using acquire_lock() as lock
                            using open_connection() as conn
                              process(conn)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_UsingWithControlFlow.
    /// </summary>

    [Fact]
    public void ParseSuflae_UsingWithControlFlow()
    {
        string source = """
                        routine test()
                          using open_file("data.txt") as file
                            for line in file.lines()
                              if line.starts_with("#")
                                continue
                              process_line(line)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_UsingWithAsync.
    /// </summary>

    [Fact]
    public void ParseSuflae_UsingWithAsync()
    {
        string source = """
                        suspended routine process_file(path: Text)
                          using waitfor async_open_file(path) as file
                            var content = waitfor file.read_all()
                            process(content)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_UsingWithPatternMatching.
    /// </summary>

    [Fact]
    public void ParseSuflae_UsingWithPatternMatching()
    {
        string source = """
                        routine test()
                          using open_file("config.json") as file
                            var config = parse_json(file.read_all())
                            when config
                              is Configuration c => apply(c)
                              else => use_default()
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_UsingDatabaseConnection.
    /// </summary>

    [Fact]
    public void ParseSuflae_UsingDatabaseConnection()
    {
        string source = """
                        routine query_users()
                          using connect_database("localhost:5432") as conn
                            var result = conn.execute("SELECT * FROM users")
                            for row in result
                              show(row.name)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_UsingWithTransaction.
    /// </summary>

    [Fact]
    public void ParseSuflae_UsingWithTransaction()
    {
        string source = """
                        routine transfer_funds(from_account: Integer, to_account: Integer, amount: Decimal)
                          using connect_database() as conn
                            using conn.begin_transaction() as tx
                              tx.execute(f"UPDATE accounts SET balance = balance - {amount} WHERE id = {from_account}")
                              tx.execute(f"UPDATE accounts SET balance = balance + {amount} WHERE id = {to_account}")
                              tx.commit()
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_UsingWithLock.
    /// </summary>

    [Fact]
    public void ParseSuflae_UsingWithLock()
    {
        string source = """
                        routine critical_section()
                          using acquire_mutex() as lock
                            increment_counter()
                            update_state()
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Complex Resource Patterns
    /// <summary>
    /// Tests ParseSuflae_MultipleFilesProcessing.
    /// </summary>

    [Fact]
    public void ParseSuflae_MultipleFilesProcessing()
    {
        string source = """
                        routine merge_files(input_paths: List[Text], output_path: Text)
                          using open_file(output_path, mode: Write) as output
                            for path in input_paths
                              using open_file(path) as input
                                output.write(input.read_all())
                                output.write("\n")
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_NetworkResourceManagement.
    /// </summary>

    [Fact]
    public void ParseSuflae_NetworkResourceManagement()
    {
        string source = """
                        suspended routine fetch_and_save(url: Text, path: Text)
                          using waitfor http.get(url) as response
                            using open_file(path, mode: Write) as file
                              file.write(response.body)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_PooledResource.
    /// </summary>

    [Fact]
    public void ParseSuflae_PooledResource()
    {
        string source = """
                        routine process_with_pool()
                          using get_connection_from_pool() as conn
                            var result = conn.query("SELECT * FROM data")
                            process(result)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Thread Spawn Tests
    /// <summary>
    /// Tests ParseSuflae_SpawnThread.
    /// </summary>

    [Fact]
    public void ParseSuflae_SpawnThread()
    {
        // Note: waitfor requires suspended routine
        string source = """
                        suspended routine start()
                          var handle = worker()
                          show("Worker started")
                          waitfor handle
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_SpawnWithArguments.
    /// </summary>

    [Fact]
    public void ParseSuflae_SpawnWithArguments()
    {
        // Note: waitfor requires suspended routine
        string source = """
                        suspended routine start()
                          var data = prepare_data()
                          var handle = process_data(data)
                          do_other_work()
                          waitfor handle
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_SpawnMultiple.
    /// </summary>

    [Fact]
    public void ParseSuflae_SpawnMultiple()
    {
        string source = """
                        suspended routine parallel_fetch(urls: List[Text]) -> List[Text]
                          var tasks = List[Task[Text]]()
                          for url in urls
                            tasks.add_last(fetch_data(url))
                          return waitfor join_all(tasks)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion
}
