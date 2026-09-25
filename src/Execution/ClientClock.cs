using System.Diagnostics;

namespace Builder.Execution;

/// <summary>
/// Dev-loop client timeline for <c>[debug] timing</c>. Each mark records the milliseconds since the PROCESS
/// started (so dotnet host startup and JIT warm-up are included), and the whole timeline prints once at the
/// end. Marks are buffered because the first ones happen before the manifest has turned timing on.
/// </summary>
internal static class ClientClock
{
    private static readonly List<(string Label, double Ms)> Marks = [];
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>Records <paramref name="label"/> at the current time since process start.</summary>
    public static void Mark(string label)
    {
        Marks.Add(item: (label, (DateTime.UtcNow - ProcessStart).TotalMilliseconds));
    }

    /// <summary>Prints the timeline to stderr when phase timing is on: each mark's absolute time and the gap
    /// since the previous mark.</summary>
    public static void Dump()
    {
        if (!Builder.Diagnostics.DiagnosticFlags.PhaseTiming || Marks.Count == 0)
        {
            return;
        }

        double previous = 0;
        foreach ((string label, double ms) in Marks)
        {
            Console.Error.WriteLine(value: $"[client] {ms,7:F1} ms (+{ms - previous,6:F1}) {label}");
            previous = ms;
        }
    }
}
