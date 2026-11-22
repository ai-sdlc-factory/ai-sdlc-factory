using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using static AiSdlcFactory.Controller.JsonFields;

namespace AiSdlcFactory.Controller;

/// <summary>How the resolver dealt with one conflicted file.</summary>
public sealed record MergeResolution(string Path, string How, string Note)
{
    public const string Combined = "combined";
    public const string KeptPullRequest = "kept_pull_request";
    public const string KeptBase = "kept_base";
    public const string Rewrote = "rewrote";

    public static readonly string[] Ways = [Combined, KeptPullRequest, KeptBase, Rewrote];
}

public sealed record MergeReport(string Verdict, string Summary, IReadOnlyList<MergeResolution> Resolutions, IReadOnlyList<string> Notes)
{
    public const string Done = "done";
    public const string Blocked = "blocked";
}

public static class MergeParser
{
    public const int MaxNote = 300;

    /// <summary>
    /// Validates the agent's answer against the files that were in conflict: every one must be answered exactly once, and nothing else. A reply that
    /// leaves one out, answers one twice or names another file is refused with the exact problem. When nothing was in conflict (the merge was clean
    /// and the agent was only asked to fix what broke) the list of resolutions must be empty.
    /// </summary>
    public static bool TryParse(string? text, IReadOnlyList<string> conflicted, [NotNullWhen(true)] out MergeReport? report, out string error) =>
        AnswerReader.TryRead<MergeReport>(text, (JsonElement root, [NotNullWhen(true)] out MergeReport? value, out string problem) => Validate(root, conflicted, out value, out problem), out report, out error);

    private static bool Validate(JsonElement root, IReadOnlyList<string> conflicted, [NotNullWhen(true)] out MergeReport? report, out string error)
    {
        report = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (MergeReport.Done or MergeReport.Blocked))
        {
            error = "\"verdict\" must be one of done, blocked";
            return false;
        }
        var summary = Text(root, "summary")?.Trim();
        if (string.IsNullOrEmpty(summary) || summary.Length > 2000)
        {
            error = "\"summary\" must be a non-empty string of at most 2000 characters";
            return false;
        }
        if (!TextList(root, "notes", out var notes, out error, maxItems: 10, maxLength: 500))
        {
            return false;
        }

        var resolutions = new List<MergeResolution>();
        if (verdict == MergeReport.Done)
        {
            if (!root.TryGetProperty("resolutions", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                error = conflicted.Count == 0
                    ? "\"resolutions\" must be an empty array, because no file was in conflict"
                    : $"\"resolutions\" must be an array with one entry for each of the {conflicted.Count} conflicted files";
                return false;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array.EnumerateArray())
            {
                var path = item.ValueKind == JsonValueKind.Object ? Text(item, "path") : null;
                var how = item.ValueKind == JsonValueKind.Object ? Text(item, "how") : null;
                var note = item.ValueKind == JsonValueKind.Object ? Text(item, "note")?.Trim() ?? "" : "";
                if (path is null || !conflicted.Contains(path, StringComparer.Ordinal))
                {
                    error = conflicted.Count == 0
                        ? "no file was in conflict, so \"resolutions\" must be empty"
                        : $"every resolution needs a \"path\" that is one of the conflicted files: {string.Join(", ", conflicted.Take(40))}";
                    return false;
                }
                if (!seen.Add(path))
                {
                    error = $"{path} is answered more than once";
                    return false;
                }
                if (how is null || !MergeResolution.Ways.Contains(how))
                {
                    error = $"resolution for {path}: \"how\" must be one of {string.Join(", ", MergeResolution.Ways)}";
                    return false;
                }
                if (note.Length > MaxNote || note.Length == 0)
                {
                    error = $"resolution for {path}: \"note\" is required and at most {MaxNote} characters";
                    return false;
                }
                resolutions.Add(new MergeResolution(path, how, note));
            }
            var missing = conflicted.Where(path => !seen.Contains(path)).ToList();
            if (missing.Count > 0)
            {
                error = $"{string.Join(", ", missing.Take(10))} {(missing.Count == 1 ? "has" : "have")} no resolution; answer every conflicted file";
                return false;
            }
        }

        report = new MergeReport(verdict, summary, resolutions.OrderBy(r => r.Path, StringComparer.Ordinal).ToList(), notes);
        return true;
    }
}
