using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

using static DevFoundry.Controller.JsonFields;

namespace DevFoundry.Controller;

/// <summary>The incident analyst's reading of the ticket: how serious, what is affected, what is missing.</summary>
public sealed record IncidentAssessment(
    string Verdict, string Summary, string Severity, string Impact, IReadOnlyList<string> Timeline,
    IReadOnlyList<string> EvidenceGaps, IReadOnlyList<string> Questions)
{
    public const string Pass = "pass";
    public const string NeedsClarification = "needs_clarification";
    public const string Reject = "reject";
}

/// <summary>A suspected cause. Evidence files and commits are checked by the factory; what it cannot find is dropped.</summary>
public sealed record Hypothesis(
    string Cause, string Confidence, IReadOnlyList<string> Evidence, IReadOnlyList<string> Commits, bool Verified = true);

public sealed record Investigation(string Verdict, string Summary, IReadOnlyList<Hypothesis> Hypotheses, IReadOnlyList<string> Questions)
{
    public const string Pass = "pass";
    public const string NeedsClarification = "needs_clarification";
}

/// <summary>One way to reduce the harm. The factory never carries these out; a person does.</summary>
public sealed record MitigationOption(
    string Kind, string Title, IReadOnlyList<string> Steps, string Risk, string Verify, bool Reversible);

public sealed record Mitigation(
    string Verdict, string Summary, IReadOnlyList<MitigationOption> Options, int Recommended, IReadOnlyList<string> Questions)
{
    public const string Pass = "pass";
    public const string NeedsClarification = "needs_clarification";
    public static readonly string[] Kinds = ["rollback", "feature_flag", "config_change", "scale", "restart", "hotfix", "monitor"];
}

/// <summary>The postmortem writer's report; the document itself is a file in the workspace.</summary>
public sealed record PostmortemDraft(string Verdict, string Summary)
{
    public const string Done = "done";
    public const string Blocked = "blocked";
}

/// <summary>Validates the incident answers. Every limit here is also written in the role files.</summary>
public static partial class IncidentParser
{
    public static readonly string[] Severities = ["low", "medium", "high", "critical"];
    public static readonly string[] Levels = ["low", "medium", "high"];
    public const int MaxText = 2000;
    public const int MaxHypotheses = 5;
    public const int MaxOptions = 4;
    public const int MaxSteps = 10;

    [GeneratedRegex(@"\A[0-9a-f]{7,40}\z")]
    private static partial Regex CommitId();

    public static bool IsCommitId(string value) => CommitId().IsMatch(value);

    public static bool TryParseAssessment(string? text, [NotNullWhen(true)] out IncidentAssessment? value, out string error) =>
        AnswerReader.TryRead<IncidentAssessment>(text, ValidateAssessment, out value, out error);

    public static bool TryParseInvestigation(string? text, [NotNullWhen(true)] out Investigation? value, out string error) =>
        AnswerReader.TryRead<Investigation>(text, ValidateInvestigation, out value, out error);

    public static bool TryParseMitigation(string? text, [NotNullWhen(true)] out Mitigation? value, out string error) =>
        AnswerReader.TryRead<Mitigation>(text, ValidateMitigation, out value, out error);

    public static bool TryParsePostmortem(string? text, [NotNullWhen(true)] out PostmortemDraft? value, out string error) =>
        AnswerReader.TryRead<PostmortemDraft>(text, ValidatePostmortem, out value, out error);

    private static bool ValidateAssessment(JsonElement root, [NotNullWhen(true)] out IncidentAssessment? value, out string error)
    {
        value = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (IncidentAssessment.Pass or IncidentAssessment.NeedsClarification or IncidentAssessment.Reject))
        {
            error = "\"verdict\" must be one of pass, needs_clarification, reject";
            return false;
        }
        if (!Required(root, "summary", out var summary, out error) || !Required(root, "impact", out var impact, out error))
        {
            return false;
        }
        var severity = Text(root, "severity");
        if (severity is null || !Severities.Contains(severity))
        {
            error = "\"severity\" must be one of low, medium, high, critical";
            return false;
        }
        if (!TextList(root, "timeline", out var timeline, out error, 15, 300)
            || !TextList(root, "evidence_gaps", out var gaps, out error, 10, 300)
            || !TextList(root, "questions", out var questions, out error, 5))
        {
            return false;
        }
        value = new IncidentAssessment(verdict, summary, severity, impact, timeline, gaps, questions);
        return true;
    }

    private static bool ValidateInvestigation(JsonElement root, [NotNullWhen(true)] out Investigation? value, out string error)
    {
        value = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (Investigation.Pass or Investigation.NeedsClarification))
        {
            error = "\"verdict\" must be one of pass, needs_clarification";
            return false;
        }
        if (!Required(root, "summary", out var summary, out error) || !TextList(root, "questions", out var questions, out error, 5))
        {
            return false;
        }
        if (!root.TryGetProperty("hypotheses", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxHypotheses)
        {
            error = $"\"hypotheses\" must be an array of at most {MaxHypotheses} objects";
            return false;
        }
        var hypotheses = new List<Hypothesis>();
        var number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            var cause = item.ValueKind == JsonValueKind.Object ? Text(item, "cause")?.Trim() : null;
            var confidence = item.ValueKind == JsonValueKind.Object ? Text(item, "confidence") : null;
            if (string.IsNullOrEmpty(cause) || cause.Length > 500 || confidence is null || !Levels.Contains(confidence))
            {
                error = $"\"hypotheses\" item {number} needs a \"cause\" of at most 500 characters and a \"confidence\" of low, medium or high";
                return false;
            }
            if (!TextList(item, "evidence", out var evidence, out var listError, 5, 200)
                || evidence.Any(path => !(SafePath.TryNormalize(path, out var normalized) && normalized == path)))
            {
                error = $"\"hypotheses\" item {number}: \"evidence\" must be at most 5 plain relative file paths" + (listError == "" ? "" : $" ({listError})");
                return false;
            }
            if (!TextList(item, "commits", out var commits, out listError, 5, 40) || commits.Any(commit => !IsCommitId(commit)))
            {
                error = $"\"hypotheses\" item {number}: \"commits\" must be at most 5 commit ids (7 to 40 lower-case hex characters)" + (listError == "" ? "" : $" ({listError})");
                return false;
            }
            hypotheses.Add(new Hypothesis(cause, confidence, evidence, commits));
        }
        if (verdict == Investigation.Pass && hypotheses.Count == 0)
        {
            error = "with verdict pass, \"hypotheses\" needs at least one entry (use needs_clarification if you cannot name a cause)";
            return false;
        }
        value = new Investigation(verdict, summary, hypotheses, questions);
        return true;
    }

    private static bool ValidateMitigation(JsonElement root, [NotNullWhen(true)] out Mitigation? value, out string error)
    {
        value = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (Mitigation.Pass or Mitigation.NeedsClarification))
        {
            error = "\"verdict\" must be one of pass, needs_clarification";
            return false;
        }
        if (!Required(root, "summary", out var summary, out error) || !TextList(root, "questions", out var questions, out error, 5))
        {
            return false;
        }
        if (!root.TryGetProperty("options", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxOptions)
        {
            error = $"\"options\" must be an array of at most {MaxOptions} objects";
            return false;
        }
        var options = new List<MitigationOption>();
        var number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            var kind = item.ValueKind == JsonValueKind.Object ? Text(item, "kind") : null;
            var title = item.ValueKind == JsonValueKind.Object ? Text(item, "title")?.Trim() : null;
            var risk = item.ValueKind == JsonValueKind.Object ? Text(item, "risk") : null;
            var verify = item.ValueKind == JsonValueKind.Object ? Text(item, "verify")?.Trim() : null;
            if (kind is null || !Mitigation.Kinds.Contains(kind))
            {
                error = $"\"options\" item {number}: \"kind\" must be one of {string.Join(", ", Mitigation.Kinds)}";
                return false;
            }
            if (string.IsNullOrEmpty(title) || title.Length > 120 || risk is null || !Levels.Contains(risk)
                || string.IsNullOrEmpty(verify) || verify.Length > 300 || !Flag(item, "reversible", out var reversible))
            {
                error = $"\"options\" item {number} needs a \"title\" (at most 120 characters), a \"risk\" (low, medium, high), a \"verify\" text (at most 300 characters) and a boolean \"reversible\"";
                return false;
            }
            if (!TextList(item, "steps", out var steps, out var listError, MaxSteps, 300) || steps.Count == 0)
            {
                error = $"\"options\" item {number}: \"steps\" must list 1 to {MaxSteps} steps of at most 300 characters" + (listError == "" ? "" : $" ({listError})");
                return false;
            }
            options.Add(new MitigationOption(kind, title, steps, risk, verify, reversible));
        }
        var recommended = root.TryGetProperty("recommended", out var rec) && rec.ValueKind == JsonValueKind.Number && rec.TryGetInt32(out var r) ? r : 0;
        if (verdict == Mitigation.Pass && (options.Count == 0 || recommended < 1 || recommended > options.Count))
        {
            error = $"with verdict pass, \"options\" needs 1 to {MaxOptions} entries and \"recommended\" must be the number (starting at 1) of one of them";
            return false;
        }
        value = new Mitigation(verdict, summary, options, verdict == Mitigation.Pass ? recommended : 0, questions);
        return true;
    }

    private static bool ValidatePostmortem(JsonElement root, [NotNullWhen(true)] out PostmortemDraft? value, out string error)
    {
        value = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (PostmortemDraft.Done or PostmortemDraft.Blocked))
        {
            error = "\"verdict\" must be one of done, blocked";
            return false;
        }
        if (!Required(root, "summary", out var summary, out error))
        {
            return false;
        }
        value = new PostmortemDraft(verdict, summary);
        return true;
    }

    private static bool Required(JsonElement root, string name, out string value, out string error)
    {
        value = Text(root, name)?.Trim() ?? "";
        error = "";
        if (value.Length is 0 or > MaxText)
        {
            error = $"\"{name}\" must be a non-empty string of at most {MaxText} characters";
            return false;
        }
        return true;
    }
}

/// <summary>Keeps only the evidence the factory could find, and says when a hypothesis is left without any.</summary>
public static class InvestigationCheck
{
    public static IReadOnlyList<string> PathsToCheck(Investigation investigation) =>
        investigation.Hypotheses.SelectMany(h => h.Evidence).Distinct(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string> CommitsToCheck(Investigation investigation) =>
        investigation.Hypotheses.SelectMany(h => h.Commits).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Commit ids are passed as arguments, never spliced into the script. A match is by prefix, as git allows.</summary>
    public static IReadOnlyList<string> CommitCommand(IReadOnlyList<string> commits) =>
        ["sh", "-c", "for c in \"$@\"; do if git cat-file -e \"$c^{commit}\" 2>/dev/null; then echo \"found $c\"; fi; done", "sh", .. commits];

    public static Investigation Apply(Investigation investigation, ISet<string> files, ISet<string> commits)
    {
        var hypotheses = investigation.Hypotheses.Select(h =>
        {
            var evidence = h.Evidence.Where(files.Contains).ToList();
            var found = h.Commits.Where(commits.Contains).ToList();
            var verified = evidence.Count + found.Count > 0;
            // A claim with nothing to point at can never be more than a guess.
            return h with { Evidence = evidence, Commits = found, Verified = verified, Confidence = verified ? h.Confidence : "low" };
        }).ToList();
        return investigation with { Hypotheses = hypotheses };
    }
}
