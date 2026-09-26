using System.Text.RegularExpressions;
using Builder.LlvmEmit;
using Builder.Targeting;
using Builder.Verification;
using Builder.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Stack-trace frames go only to routines that can crash, directly or through their callees (see
/// CrashReachability). A routine that cannot crash never shows up in a crash trace, and its frame used to
/// cost a push/pop plus location stores per call even after inlining.
/// </summary>
public class TraceFrameTests
{
    private const string Source = """
                                  import IO/Console

                                  routine pure_mix(a: U64) -> U64
                                    return (a >>> 1) ^ 5_u64

                                  routine checked_sum(a: U64, b: U64) -> U64
                                    return a + b

                                  routine calls_checked(a: U64) -> U64
                                    return checked_sum(a: a, b: 1_u64)

                                  routine start()
                                    show(pure_mix(a: 3_u64) +% calls_checked(a: 2_u64))
                                    return
                                  """;

    [Fact]
    public void RoutineThatCannotCrash_HasNoTraceFrame()
    {
        Assert.DoesNotContain(expectedSubstring: "@_rf_trace_push", actualString: Body(name: "pure_mix("));
    }

    [Fact]
    public void RoutineThatCanCrash_KeepsItsTraceFrame()
    {
        Assert.Contains(expectedSubstring: "@_rf_trace_push", actualString: Body(name: "checked_sum("));
    }

    /// <summary>A routine that only calls something that can crash is on the crash path, so it keeps its frame.</summary>
    [Fact]
    public void RoutineThatCallsACrashingRoutine_KeepsItsTraceFrame()
    {
        Assert.Contains(expectedSubstring: "@_rf_trace_push", actualString: Body(name: "calls_checked("));
    }

    private static string Body(string name)
    {
        Program program = Parse(source: Source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge, buildMode: RfBuildMode.Release);
        AnalysisResult result = analyzer.Analyze(program: program);
        Assert.Empty(collection: result.Errors);
        string ir = new LlvmEmitter(program: program,
            registry: result.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                BuildMode = RfBuildMode.Release,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            }).Generate();
        Match m = Regex.Match(input: ir,
            pattern: @"^define [^\n]*" + Regex.Escape(str: name) + @"[^\n]*\{\n(.*?)^\}",
            options: RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(condition: m.Success, userMessage: $"no definition of {name} in the IR");
        return m.Groups[groupnum: 1].Value;
    }
}
