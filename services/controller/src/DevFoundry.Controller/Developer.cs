using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using static DevFoundry.Controller.JsonFields;

namespace DevFoundry.Controller;

/// <summary>
/// The developer's own account of its work. It is only a summary: what really changed is read from the
/// workspace (<see cref="ChangeReport"/>), never taken from this.
/// </summary>
public sealed record DeveloperReport(string Verdict, string Summary, IReadOnlyList<string> Notes)
{
    public const string Done = "done";
    public const string Blocked = "blocked";
}

public static class DeveloperParser
{
    public static bool TryParse(string? text, [NotNullWhen(true)] out DeveloperReport? report, out string error)
    {
        report = null;
        string? lastError = null;
        foreach (var candidate in JsonObjects.TopLevel(text ?? "").Reverse())
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(candidate);
            }
            catch (JsonException)
            {
                continue;
            }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("verdict", out _))
                {
                    continue;
                }
                var verdict = Text(root, "verdict");
                var summary = Text(root, "summary")?.Trim();
                if (verdict is not (DeveloperReport.Done or DeveloperReport.Blocked))
                {
                    lastError = "\"verdict\" must be done or blocked";
                }
                else if (string.IsNullOrEmpty(summary) || summary.Length > 2000)
                {
                    lastError = "\"summary\" must be a non-empty string of at most 2000 characters";
                }
                else if (!TextList(root, "notes", out var notes, out var listError, maxItems: 10))
                {
                    lastError = listError;
                }
                else if (verdict == DeveloperReport.Blocked && notes.Count == 0)
                {
                    lastError = "a blocked verdict needs at least one note saying what blocks you";
                }
                else
                {
                    report = new DeveloperReport(verdict, summary, notes);
                    error = "";
                    return true;
                }
            }
        }
        error = lastError ?? "no JSON object with a \"verdict\" field was found";
        return false;
    }
}

public sealed record GateResult(string Name, bool Passed, long ExitCode, bool TimedOut, string Output);
