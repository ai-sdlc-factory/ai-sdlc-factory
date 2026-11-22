using System.Text.RegularExpressions;

namespace AiSdlcFactory.Controller;

/// <summary>
/// How the QA report on a ticket marks the findings a person can have fixed with <c>/fix qa</c>, and how they are read back from the comment.
/// The comment is the only record: nothing about a QA run is kept anywhere else.
/// </summary>
public static partial class QaReportFormat
{
    /// <summary>Hidden marker on a QA report, so the latest one can be found.</summary>
    public const string Marker = "<!-- ai-sdlc-factory:qa-report -->";

    public const string FindingsHeading = "### Findings to fix";

    public const int MaxFindings = 20;

    [GeneratedRegex(@"\A\d{1,2}\.\s+(?<text>\S.*)\z")]
    private static partial Regex Numbered();

    /// <summary>The findings of a QA report comment, in order; empty when it has none.</summary>
    public static IReadOnlyList<string> ExtractFindings(string? comment)
    {
        var result = new List<string>();
        if (comment is null || !comment.Contains(Marker, StringComparison.Ordinal))
        {
            return result;
        }
        var inSection = false;
        foreach (var raw in comment.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line == FindingsHeading)
            {
                inSection = true;
                continue;
            }
            if (!inSection)
            {
                continue;
            }
            if (line.Length == 0)
            {
                if (result.Count > 0)
                {
                    break;
                }
                continue;
            }
            if (Numbered().Match(line) is { Success: true } match && result.Count < MaxFindings)
            {
                result.Add(match.Groups["text"].Value);
            }
            else
            {
                break;
            }
        }
        return result;
    }

    /// <summary>The line that says what a QA run was asked to cover; the report, the notice and the blocked comment all start with it, so a person always sees the scope.</summary>
    public static string ScopeLine(string mode) =>
        mode == QaRequest.Full
            ? "**Scope:** the whole application and this pull request's change."
            : "**Scope:** this pull request's change.";

    /// <summary>The instruction after <c>/fix</c> that means "fix what the latest QA report found".</summary>
    public static bool IsQaFix(string? instruction) => string.Equals(instruction?.Trim(), "qa", StringComparison.OrdinalIgnoreCase);
}
