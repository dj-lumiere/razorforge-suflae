using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RazorForge.Tests.Meta;

/// <summary>
/// Tests that test methods contain an assertion path.
/// </summary>
public sealed partial class AssertionPresenceTests
{
    private static readonly Regex TestAttributePattern = MyRegex();

    private static readonly Regex MethodPattern = MyRegex1();

    private static readonly string[] AssertionMarkers =
    [
        "Assert.",
        "Record.Exception",
        "AssertParses",
        "AssertParsesSuflae",
        "AssertParseError",
        "AssertAnalyzes",
        "AssertAnalyzesSuflae",
        "AssertHasError",
        "AssertHasErrorSuflae"
    ];

    /// <summary>
    /// Verifies that the test validates methods contain assertion path.
    /// </summary>
    [Fact]
    public void TestMethods_ContainAssertionPath()
    {
        string testRoot = FindTestRoot();
        List<string> methodsWithoutAssertions = Directory.EnumerateFiles(
                                                              path: testRoot,
                                                              searchPattern: "*.cs",
                                                              searchOption: SearchOption
                                                                 .AllDirectories)
                                                         .Where(predicate: path => !path.EndsWith(
                                                              value:
                                                              $"{Path.DirectorySeparatorChar}GlobalUsings.cs",
                                                              comparisonType: StringComparison
                                                                 .Ordinal))
                                                         .SelectMany(
                                                              selector:
                                                              FindTestMethodsWithoutAssertions)
                                                         .ToList();

        Assert.Empty(collection: methodsWithoutAssertions);
    }

    private static IEnumerable<string> FindTestMethodsWithoutAssertions(string filePath)
    {
        string[] lines = File.ReadAllLines(path: filePath);

        for (int i = 0; i < lines.Length; i++)
        {
            if (!TestAttributePattern.IsMatch(input: lines[i]))
            {
                continue;
            }

            int methodLine = FindNextMethodLine(lines: lines, startLine: i + 1);
            if (methodLine < 0)
            {
                continue;
            }

            string methodName = MethodPattern.Match(input: lines[methodLine])
                                             .Groups["name"].Value;
            string methodBody = ExtractMethodBody(lines: lines, methodLine: methodLine);

            if (!AssertionMarkers.Any(predicate: marker => methodBody.Contains(
                    value: marker,
                    comparisonType: StringComparison.Ordinal)))
            {
                yield return
                    $"{Path.GetRelativePath(relativeTo: FindTestRoot(), path: filePath)}::{methodName}";
            }
        }
    }

    private static int FindNextMethodLine(string[] lines, int startLine)
    {
        for (int i = startLine; i < lines.Length; i++)
        {
            if (MethodPattern.IsMatch(input: lines[i]))
            {
                return i;
            }

            if (TestAttributePattern.IsMatch(input: lines[i]))
            {
                return -1;
            }
        }

        return -1;
    }

    private static string ExtractMethodBody(string[] lines, int methodLine)
    {
        var bodyLines = new List<string>();
        int braceDepth = 0;
        bool foundOpeningBrace = false;

        for (int i = methodLine; i < lines.Length; i++)
        {
            string line = lines[i];
            bodyLines.Add(item: line);

            foreach (char ch in line)
            {
                if (ch == '{')
                {
                    braceDepth++;
                    foundOpeningBrace = true;
                }
                else if (ch == '}')
                {
                    braceDepth--;
                }
            }

            if (foundOpeningBrace && braceDepth == 0)
            {
                break;
            }
        }

        return string.Join(separator: Environment.NewLine, values: bodyLines);
    }

    private static string FindTestRoot()
    {
        string current = AppContext.BaseDirectory;

        while (!string.IsNullOrEmpty(value: current))
        {
            string candidate = Path.Combine(path1: current, path2: "tests");
            if (Directory.Exists(path: candidate) &&
                File.Exists(path: Path.Combine(path1: candidate, path2: "TestHelpers.cs")))
            {
                return candidate;
            }

            DirectoryInfo? parent = Directory.GetParent(path: current);
            current = parent?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException(message: "Could not find the tests directory.");
    }

    [GeneratedRegex(@"^\s*\[(?:Fact|Theory)\b", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
    [GeneratedRegex(
        @"^\s*public\s+(?:async\s+)?(?:Task|void)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled)]
    private static partial Regex MyRegex1();
}
