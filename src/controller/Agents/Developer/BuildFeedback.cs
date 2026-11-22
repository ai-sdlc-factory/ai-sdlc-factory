using System.Text;

namespace AiSdlcFactory.Controller;

/// <summary>Turns check results into the feedback the next round gets, and into short ticket lines.</summary>
public static class BuildFeedback
{
    private const int MaxOutputPerGate = 1800;

    /// <summary>Failed checks with the end of their output, where the error usually is. Null if nothing failed.</summary>
    public static string? FromGates(IEnumerable<GateResult> results)
    {
        var failed = results.Where(result => !result.Passed).ToList();
        if (failed.Count == 0)
        {
            return null;
        }
        var text = new StringBuilder();
        foreach (var result in failed)
        {
            var output = result.Output.Trim();
            text.AppendLine($"Check \"{result.Name}\" {(result.TimedOut ? "timed out" : $"failed with exit code {result.ExitCode}")}:");
            text.AppendLine(output.Length > MaxOutputPerGate ? "[start of output cut]\n" + output[^MaxOutputPerGate..] : output);
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    public static string FromViolations(IEnumerable<string> violations) =>
        "The changes cannot be accepted:\n" + string.Join('\n', violations.Take(10).Select(v => $"- {v}"));

    public static string Summary(IEnumerable<GateResult> results) =>
        string.Join(", ", results.Select(result => $"{result.Name} {(result.Passed ? "passed" : result.TimedOut ? "timed out" : "failed")}"));
}
