using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using static DevFoundry.Controller.JsonFields;

namespace DevFoundry.Controller;

/// <summary>The bug analyst's answer: is the report clear, and what behaviour is wrong.</summary>
public sealed record BugAnalysis(
    string Verdict, string Summary, string Expected, string Actual, IReadOnlyList<string> Hints, string Severity,
    IReadOnlyList<string> Questions)
{
    public const string Pass = "pass";
    public const string NeedsClarification = "needs_clarification";
    public const string Reject = "reject";
}

/// <summary>
/// The reproducer's answer: a test that should fail because of the bug. The factory runs <see cref="TestCommand"/>
/// on the unchanged code and requires it to fail; the agent's word that it fails is not enough.
/// </summary>
public sealed record Reproduction(
    string Verdict, string Summary, IReadOnlyList<string> TestCommand, IReadOnlyList<string> TestFiles,
    IReadOnlyList<string> Questions)
{
    public const string Reproduced = "reproduced";
    public const string CannotReproduce = "cannot_reproduce";
}

public sealed record CodeLocation(string Path, string Note);

/// <summary>The diagnostician's answer: where the cause is, and whether the fix is small enough for this workflow.</summary>
public sealed record Diagnosis(
    string Verdict, string RootCause, IReadOnlyList<CodeLocation> Locations, string FixOutline, IReadOnlyList<string> Questions)
{
    public const string Pass = "pass";
    public const string NeedsClarification = "needs_clarification";
    public const string TooLarge = "too_large";
}

/// <summary>Validates the three bug-related answers. Every limit here is also written in the role file.</summary>
public static class BugParser
{
    public const int MaxText = 2000;
    public const int MaxCommandArguments = 32;
    public const int MaxArgumentLength = 500;
    public const int MaxTestFiles = 10;
    public const int MaxLocations = 10;
    public static readonly string[] Severities = ["low", "medium", "high"];

    public static bool TryParseAnalysis(string? text, [NotNullWhen(true)] out BugAnalysis? value, out string error) =>
        AnswerReader.TryRead<BugAnalysis>(text, ValidateAnalysis, out value, out error);

    public static bool TryParseReproduction(string? text, [NotNullWhen(true)] out Reproduction? value, out string error) =>
        AnswerReader.TryRead<Reproduction>(text, ValidateReproduction, out value, out error);

    public static bool TryParseDiagnosis(string? text, [NotNullWhen(true)] out Diagnosis? value, out string error) =>
        AnswerReader.TryRead<Diagnosis>(text, ValidateDiagnosis, out value, out error);

    private static bool ValidateAnalysis(JsonElement root, [NotNullWhen(true)] out BugAnalysis? value, out string error)
    {
        value = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (BugAnalysis.Pass or BugAnalysis.NeedsClarification or BugAnalysis.Reject))
        {
            error = "\"verdict\" must be one of pass, needs_clarification, reject";
            return false;
        }
        if (!Required(root, "summary", out var summary, out error) || !Required(root, "expected", out var expected, out error)
            || !Required(root, "actual", out var actual, out error))
        {
            return false;
        }
        var severity = Text(root, "severity");
        if (severity is null || !Severities.Contains(severity))
        {
            error = "\"severity\" must be one of low, medium, high";
            return false;
        }
        if (!TextList(root, "hints", out var hints, out error, maxItems: 10) || !TextList(root, "questions", out var questions, out error, maxItems: 5))
        {
            return false;
        }
        value = new BugAnalysis(verdict, summary, expected, actual, hints, severity, questions);
        return true;
    }

    private static bool ValidateReproduction(JsonElement root, [NotNullWhen(true)] out Reproduction? value, out string error)
    {
        value = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (Reproduction.Reproduced or Reproduction.CannotReproduce))
        {
            error = "\"verdict\" must be one of reproduced, cannot_reproduce";
            return false;
        }
        if (!Required(root, "summary", out var summary, out error) || !TextList(root, "questions", out var questions, out error, maxItems: 5))
        {
            return false;
        }
        if (verdict == Reproduction.CannotReproduce)
        {
            value = new Reproduction(verdict, summary, [], [], questions);
            return true;
        }

        if (!TextList(root, "test_command", out var command, out error, MaxCommandArguments, MaxArgumentLength) || command.Count == 0)
        {
            error = command.Count == 0 && error == ""
                ? "\"test_command\" must list the command that runs the new test, one argument per item"
                : error;
            return false;
        }
        if (!TextList(root, "test_files", out var files, out error, MaxTestFiles, 200))
        {
            return false;
        }
        if (files.Count == 0 || files.Any(file => !IsPlain(file)))
        {
            error = $"\"test_files\" must list 1 to {MaxTestFiles} plain relative paths of the test files you wrote or changed";
            return false;
        }
        value = new Reproduction(verdict, summary, command, files, questions);
        return true;
    }

    private static bool ValidateDiagnosis(JsonElement root, [NotNullWhen(true)] out Diagnosis? value, out string error)
    {
        value = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (Diagnosis.Pass or Diagnosis.NeedsClarification or Diagnosis.TooLarge))
        {
            error = "\"verdict\" must be one of pass, needs_clarification, too_large";
            return false;
        }
        if (!Required(root, "root_cause", out var cause, out error) || !Required(root, "fix_outline", out var outline, out error)
            || !TextList(root, "questions", out var questions, out error, maxItems: 5))
        {
            return false;
        }
        var locations = new List<CodeLocation>();
        if (!root.TryGetProperty("locations", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxLocations)
        {
            error = $"\"locations\" must be an array of at most {MaxLocations} objects with \"path\" and \"note\"";
            return false;
        }
        var number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            var path = item.ValueKind == JsonValueKind.Object ? Text(item, "path")?.Trim() : null;
            var note = item.ValueKind == JsonValueKind.Object ? Text(item, "note")?.Trim() : null;
            if (path is null || !IsPlain(path) || string.IsNullOrEmpty(note) || note.Length > 300)
            {
                error = $"\"locations\" item {number} needs a plain relative \"path\" and a \"note\" of at most 300 characters";
                return false;
            }
            locations.Add(new CodeLocation(path, note));
        }
        value = new Diagnosis(verdict, cause, locations, outline, questions);
        return true;
    }

    private static bool IsPlain(string path) => SafePath.TryNormalize(path, out var normalized) && normalized == path;

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
