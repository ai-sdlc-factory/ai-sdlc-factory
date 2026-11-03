using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using static DevFoundry.Controller.JsonFields;

namespace DevFoundry.Controller;

public static class Severity
{
    public const string Blocker = "blocker";
    public const string Major = "major";
    public const string Minor = "minor";
    public const string Nit = "nit";

    public static bool IsBlocking(string severity) => severity is Blocker or Major;
}

public sealed record ReviewFinding(string Severity, string? Path, string Message);

/// <summary>A reviewer's validated answer. Blocking findings, not the reviewer's say-so, decide the verdict.</summary>
public sealed record ReviewReport(string Verdict, string Summary, IReadOnlyList<ReviewFinding> Findings)
{
    public const string Pass = "pass";
    public const string NeedsChanges = "needs_changes";

    public IEnumerable<ReviewFinding> Blocking => Findings.Where(f => Severity.IsBlocking(f.Severity));
    public IEnumerable<ReviewFinding> Advice => Findings.Where(f => !Severity.IsBlocking(f.Severity));

    public static string Derive(IEnumerable<ReviewFinding> findings) =>
        findings.Any(f => Severity.IsBlocking(f.Severity)) ? NeedsChanges : Pass;
}

public static class ReviewParser
{
    public const int MaxFindings = 20;
    public const int MaxMessage = 500;

    public static bool TryParse(string? text, [NotNullWhen(true)] out ReviewReport? report, out string error)
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
                if (Validate(document.RootElement, out report, out var problem))
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

    private static bool Validate(JsonElement root, [NotNullWhen(true)] out ReviewReport? report, out string error)
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
        if (!root.TryGetProperty("findings", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            error = "\"findings\" must be an array";
            return false;
        }
        if (array.GetArrayLength() > MaxFindings)
        {
            error = $"\"findings\" has {array.GetArrayLength()} entries, the limit is {MaxFindings}; report the most important ones";
            return false;
        }

        var findings = new List<ReviewFinding>();
        var number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            var severity = item.ValueKind == JsonValueKind.Object ? Text(item, "severity") : null;
            if (severity is not (Severity.Blocker or Severity.Major or Severity.Minor or Severity.Nit))
            {
                error = $"finding {number}: \"severity\" must be blocker, major, minor or nit";
                return false;
            }
            var message = Text(item, "message")?.Trim();
            if (string.IsNullOrEmpty(message) || message.Length > MaxMessage)
            {
                error = $"finding {number}: \"message\" must be a non-empty string of at most {MaxMessage} characters (it is {message?.Length ?? 0})";
                return false;
            }
            string? path = null;
            if (item.TryGetProperty("path", out var rawPath) && rawPath.ValueKind != JsonValueKind.Null)
            {
                if (rawPath.ValueKind != JsonValueKind.String || !SafePath.TryNormalize(rawPath.GetString()!, out var normal))
                {
                    error = $"finding {number}: \"path\" must be a plain relative file path, or left out";
                    return false;
                }
                path = normal;
            }
            findings.Add(new ReviewFinding(severity, path, message));
        }

        var derived = ReviewReport.Derive(findings);
        if (derived != verdict)
        {
            error = verdict == ReviewReport.Pass
                ? "\"verdict\" is pass but there are blocker or major findings; the verdict must be needs_changes"
                : "\"verdict\" is needs_changes but there is no blocker or major finding; list one or answer pass";
            return false;
        }
        report = new ReviewReport(verdict, summary, findings);
        return true;
    }
}

public static class QaStatus
{
    public const string Met = "met";
    public const string NotMet = "not_met";
}

public sealed record QaCriterion(int Index, string Criterion, string Status, IReadOnlyList<string> Evidence, string Note);

/// <summary>QA's per-criterion answer. The verdict follows from the statuses; evidence for "met" is verified.</summary>
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
