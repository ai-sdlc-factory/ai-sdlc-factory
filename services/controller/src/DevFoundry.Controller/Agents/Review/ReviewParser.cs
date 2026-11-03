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
