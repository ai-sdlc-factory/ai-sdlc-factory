using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AiSdlcFactory.Controller;

public static class CoverageStatus
{
    public const string Covered = "covered";
    public const string Partial = "partial";
    public const string Missing = "missing";
}

public static class CoverageVerdicts
{
    public const string NotCovered = "not_covered";
    public const string PartlyCovered = "partly_covered";
    public const string FullyCovered = "fully_covered";

    /// <summary>The overall verdict is derived from the per-criterion statuses, never taken on trust.</summary>
    public static string Derive(IEnumerable<string> statuses)
    {
        var list = statuses.ToList();
        if (list.Count > 0 && list.All(status => status == CoverageStatus.Covered))
        {
            return FullyCovered;
        }
        return list.All(status => status == CoverageStatus.Missing) ? NotCovered : PartlyCovered;
    }
}

/// <summary>One of the analyst's acceptance criteria, judged against the existing code.</summary>
public sealed record CriterionCoverage(
    int Index, string Criterion, string Status, IReadOnlyList<string> Evidence, string Note);

/// <summary>The coverage agent's validated answer. <see cref="Criteria"/> follow the analyst's order.</summary>
public sealed record CoverageReport(string Verdict, string Summary, IReadOnlyList<CriterionCoverage> Criteria)
{
    public IEnumerable<CriterionCoverage> Remaining => Criteria.Where(c => c.Status != CoverageStatus.Covered);
}

public static class CoverageParser
{
    private const int MaxEvidencePerCriterion = 3;
    private const int MaxNote = 300;

    public static bool TryParse(
        string? text, IReadOnlyList<string> criteria, [NotNullWhen(true)] out CoverageReport? report, out string error)
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
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("verdict", out _))
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

    private static bool Validate(
        JsonElement root, IReadOnlyList<string> criteria, [NotNullWhen(true)] out CoverageReport? report, out string error)
    {
        report = null;
        error = "";
        var verdict = root.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (verdict is not (CoverageVerdicts.NotCovered or CoverageVerdicts.PartlyCovered or CoverageVerdicts.FullyCovered))
        {
            error = "\"verdict\" must be one of not_covered, partly_covered, fully_covered";
            return false;
        }
        var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(summary) || summary.Length > 2000)
        {
            error = "\"summary\" must be a non-empty string of at most 2000 characters";
            return false;
        }
        if (!root.TryGetProperty("criteria", out var array) || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() != criteria.Count)
        {
            error = $"\"criteria\" must be an array with exactly one entry for each of the {criteria.Count} acceptance criteria";
            return false;
        }

        var byIndex = new Dictionary<int, CriterionCoverage>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number
                || !index.TryGetInt32(out var number) || number < 1 || number > criteria.Count
                || byIndex.ContainsKey(number))
            {
                error = $"every entry of \"criteria\" needs a distinct \"index\" from 1 to {criteria.Count}";
                return false;
            }
            var status = item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            if (status is not (CoverageStatus.Covered or CoverageStatus.Partial or CoverageStatus.Missing))
            {
                error = $"criterion {number}: \"status\" must be covered, partial or missing";
                return false;
            }
            if (!Evidence(item, out var evidence))
            {
                error = $"criterion {number}: \"evidence\" must be an array of relative file paths such as src/app.py or src/app.py:12";
                return false;
            }
            if (status != CoverageStatus.Missing && evidence.Count == 0)
            {
                error = $"criterion {number}: a {status} criterion needs at least one evidence path";
                return false;
            }
            var note = item.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
            if (note.Length > MaxNote)
            {
                error = $"criterion {number}: \"note\" is longer than {MaxNote} characters";
                return false;
            }
            byIndex[number] = new CriterionCoverage(number, criteria[number - 1], status, evidence, note);
        }

        var ordered = Enumerable.Range(1, criteria.Count).Select(i => byIndex[i]).ToList();
        var derived = CoverageVerdicts.Derive(ordered.Select(c => c.Status));
        if (derived != verdict)
        {
            error = $"\"verdict\" is {verdict} but the criteria statuses mean {derived}";
            return false;
        }
        report = new CoverageReport(verdict, summary, ordered);
        return true;
    }

    private static bool Evidence(JsonElement item, out IReadOnlyList<string> evidence)
    {
        var paths = new List<string>();
        evidence = paths;
        if (!item.TryGetProperty("evidence", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            if (!SafePath.TryNormalize(entry.GetString()!, out var path))
            {
                return false;
            }
            if (!paths.Contains(path) && paths.Count < MaxEvidencePerCriterion)
            {
                paths.Add(path);
            }
        }
        return true;
    }
}
