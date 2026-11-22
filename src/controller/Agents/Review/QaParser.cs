using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using static AiSdlcFactory.Controller.JsonFields;

namespace AiSdlcFactory.Controller;

public static class QaStatus
{
    public const string Met = "met";
    public const string NotMet = "not_met";
}

public sealed record QaCriterion(int Index, string Criterion, string Status, IReadOnlyList<string> Evidence, string Note);

/// <summary>The acceptance check's per-criterion answer. The verdict follows from the statuses; evidence for "met" is verified.</summary>
public sealed record QaReport(string Verdict, string Summary, IReadOnlyList<QaCriterion> Criteria)
{
    public IEnumerable<QaCriterion> NotMet => Criteria.Where(c => c.Status == QaStatus.NotMet);

    public static string Derive(IEnumerable<string> statuses) =>
        statuses.All(status => status == QaStatus.Met) ? ReviewReport.Pass : ReviewReport.NeedsChanges;
}

public static class QaParser
{
    public static bool TryParse(
        string? text, IReadOnlyList<string> criteria, [NotNullWhen(true)] out QaReport? report, out string error)
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
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("verdict", out _))
                {
                    continue;
                }
                if (Validate(document.RootElement, criteria, out report, out var problem))
                {
                    error = "";
                    return true;
                }
                lastError = problem;
            }
        }
        error = lastError ?? "no JSON object with a \"verdict\" field was found";
        return false;
    }

    private static bool Validate(JsonElement root, IReadOnlyList<string> criteria, [NotNullWhen(true)] out QaReport? report, out string error)
    {
        report = null;
        error = "";
        var verdict = Text(root, "verdict");
        if (verdict is not (ReviewReport.Pass or ReviewReport.NeedsChanges))
        {
            error = "\"verdict\" must be pass or needs_changes";
            return false;
        }
        var summary = Text(root, "summary")?.Trim();
        if (string.IsNullOrEmpty(summary) || summary.Length > 2000)
        {
            error = "\"summary\" must be a non-empty string of at most 2000 characters";
            return false;
        }
        if (!root.TryGetProperty("criteria", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != criteria.Count)
        {
            error = $"\"criteria\" must be an array with exactly one entry for each of the {criteria.Count} acceptance criteria";
            return false;
        }

        var byIndex = new Dictionary<int, QaCriterion>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var rawIndex)
                || rawIndex.ValueKind != JsonValueKind.Number || !rawIndex.TryGetInt32(out var index)
                || index < 1 || index > criteria.Count || byIndex.ContainsKey(index))
            {
                error = $"every entry of \"criteria\" needs a distinct \"index\" from 1 to {criteria.Count}";
                return false;
            }
            var status = Text(item, "status");
            if (status is not (QaStatus.Met or QaStatus.NotMet))
            {
                error = $"criterion {index}: \"status\" must be met or not_met";
                return false;
            }
            if (!EvidenceList(item, out var evidence))
            {
                error = $"criterion {index}: \"evidence\" must be an array of at most 3 plain relative file paths (a test or the code that shows the criterion)";
                return false;
            }
            var note = Text(item, "note")?.Trim() ?? "";
            if (note.Length > 300)
            {
                error = $"criterion {index}: \"note\" is {note.Length} characters, the limit is 300";
                return false;
            }
            if (status == QaStatus.Met && evidence.Count == 0)
            {
                error = $"criterion {index}: a met criterion needs at least one evidence path";
                return false;
            }
            if (status == QaStatus.NotMet && note.Length == 0)
            {
                error = $"criterion {index}: a not_met criterion needs a \"note\" saying what is missing";
                return false;
            }
            byIndex[index] = new QaCriterion(index, criteria[index - 1], status, evidence, note);
        }

        var ordered = Enumerable.Range(1, criteria.Count).Select(i => byIndex[i]).ToList();
        var derived = QaReport.Derive(ordered.Select(c => c.Status));
        if (derived != verdict)
        {
            error = $"\"verdict\" is {verdict} but the criteria statuses mean {derived}";
            return false;
        }
        report = new QaReport(verdict, summary, ordered);
        return true;
    }

    private static bool EvidenceList(JsonElement item, out IReadOnlyList<string> evidence)
    {
        var paths = new List<string>();
        evidence = paths;
        if (!item.TryGetProperty("evidence", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || !SafePath.TryNormalize(entry.GetString()!, out var path))
            {
                return false;
            }
            if (!paths.Contains(path) && paths.Count < 3)
            {
                paths.Add(path);
            }
        }
        return true;
    }

    public static IReadOnlyList<string> PathsToCheck(QaReport report) =>
        report.Criteria.SelectMany(c => c.Evidence).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>A "met" criterion whose cited files do not exist is not met; the verdict is derived again.</summary>
    public static QaReport Verify(QaReport report, ISet<string> existing)
    {
        var criteria = report.Criteria.Select(criterion =>
        {
            var evidence = criterion.Evidence.Where(existing.Contains).ToList();
            if (criterion.Status == QaStatus.Met && evidence.Count == 0)
            {
                var note = string.IsNullOrEmpty(criterion.Note) ? "cited files were not found" : $"{criterion.Note} (cited files were not found)";
                return criterion with { Status = QaStatus.NotMet, Evidence = evidence, Note = note };
            }
            return criterion with { Evidence = evidence };
        }).ToList();
        return report with { Verdict = QaReport.Derive(criteria.Select(c => c.Status)), Criteria = criteria };
    }
}
