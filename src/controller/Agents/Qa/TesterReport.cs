using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using static AiSdlcFactory.Controller.JsonFields;

namespace AiSdlcFactory.Controller;

/// <summary>One thing the tester tried, as a test: its name, the suite it belongs to, and the acceptance criteria it covers (by number).</summary>
public sealed record TestedScenario(string Name, string Suite, IReadOnlyList<int> Covers);

/// <summary>A problem the tester saw while trying the application, with the steps that show it. A finding is the tester's claim, not a verified fact.</summary>
public sealed record TesterFinding(string Severity, string Message, string Steps);

/// <summary>After a failing run: whether the tester thinks a test failed because of the application or because the test itself is wrong.</summary>
public sealed record FailureAssessment(string Test, string Verdict, string Explanation);

/// <summary>
/// The tester's answer. The tests it wrote are run by the factory itself, so what the tester says about them is advice; what the run shows is the fact.
/// </summary>
public sealed record TesterReport(
    string Verdict, string Summary, IReadOnlyList<TestedScenario> Scenarios, IReadOnlyList<string> Files,
    IReadOnlyList<TesterFinding> Findings, IReadOnlyList<FailureAssessment> Assessments)
{
    public const string Written = "tests_written";
    public const string Blocked = "blocked";

    public const string ApplicationBug = "application_bug";
    public const string TestProblem = "test_problem";

    /// <summary>The tester says every failing test fails because of the application, so another round would only repeat the run.</summary>
    public bool BlamesOnlyTheApplication => Assessments.Count > 0 && Assessments.All(a => a.Verdict == ApplicationBug);
}

/// <summary>Validates the tester's answer. Every limit here is also written in the role file.</summary>
public static class TesterParser
{
    public const int MaxText = 2000;
    public const int MaxScenarios = 30;
    public const int MaxFiles = 30;
    public const int MaxFindings = 10;
    public const int MaxAssessments = 20;
    public static readonly string[] Severities = ["blocker", "major", "minor", "nit"];

    /// <summary>The shape a workflow stage wants: the value, or the reason it cannot be used.</summary>
    public static (TesterReport? Value, string? Error) Parse(string text) =>
        TryParse(text, out var report, out var error) ? (report, null) : (null, error);

    public static bool TryParse(string? text, [NotNullWhen(true)] out TesterReport? value, out string error) =>
        AnswerReader.TryRead<TesterReport>(text, Validate, out value, out error);

    private static bool Validate(JsonElement root, [NotNullWhen(true)] out TesterReport? value, out string error)
    {
        value = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (TesterReport.Written or TesterReport.Blocked))
        {
            error = "\"verdict\" must be one of tests_written, blocked";
            return false;
        }
        var summary = Text(root, "summary")?.Trim() ?? "";
        if (summary.Length is 0 or > MaxText)
        {
            error = $"\"summary\" must be a non-empty string of at most {MaxText} characters";
            return false;
        }
        if (verdict == TesterReport.Blocked)
        {
            value = new TesterReport(verdict, summary, [], [], [], []);
            error = "";
            return true;
        }

        if (!ReadScenarios(root, out var scenarios, out error)
            || !TextList(root, "files", out var files, out error, MaxFiles, 200)
            || !ReadFindings(root, out var findings, out error)
            || !ReadAssessments(root, out var assessments, out error))
        {
            return false;
        }
        if (scenarios.Count == 0)
        {
            error = "\"scenarios\" must list at least one scenario you wrote a test for";
            return false;
        }
        if (files.Any(file => !SafePath.TryNormalize(file, out var normalized) || normalized != file))
        {
            error = "\"files\" must be plain relative paths of the files you wrote or changed (letters, digits and . _ - @ + /, no leading slash and no ..)";
            return false;
        }
        value = new TesterReport(verdict, summary, scenarios, files, findings, assessments);
        return true;
    }

    private static bool ReadScenarios(JsonElement root, out List<TestedScenario> scenarios, out string error)
    {
        scenarios = [];
        error = "";
        if (!root.TryGetProperty("scenarios", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxScenarios)
        {
            error = $"\"scenarios\" must be an array of at most {MaxScenarios} objects with \"name\", \"suite\" (api or ui) and \"covers\" (criterion numbers)";
            return false;
        }
        var number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            var name = item.ValueKind == JsonValueKind.Object ? Text(item, "name")?.Trim() : null;
            var suite = item.ValueKind == JsonValueKind.Object ? Text(item, "suite") : null;
            var covers = new List<int>();
            var coversOk = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("covers", out var coversValue) && coversValue.ValueKind == JsonValueKind.Array
                && coversValue.GetArrayLength() <= 20
                && coversValue.EnumerateArray().All(c => c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var index) && index is >= 1 and <= 99 && AddTo(covers, index));
            if (string.IsNullOrEmpty(name) || name.Length > 200 || suite is not (VerifyConfig.Api or VerifyConfig.Ui) || !coversOk)
            {
                error = $"\"scenarios\" item {number} needs a \"name\" of at most 200 characters, a \"suite\" of api or ui, and \"covers\": an array of criterion numbers (it may be empty)";
                return false;
            }
            scenarios.Add(new TestedScenario(name, suite, covers));
        }
        return true;
    }

    private static bool AddTo(List<int> list, int value)
    {
        list.Add(value);
        return true;
    }

    private static bool ReadFindings(JsonElement root, out List<TesterFinding> findings, out string error)
    {
        findings = [];
        error = "";
        if (!root.TryGetProperty("findings", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxFindings)
        {
            error = $"\"findings\" must be an array of at most {MaxFindings} objects with \"severity\", \"message\" and \"steps\"";
            return false;
        }
        var number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            var severity = item.ValueKind == JsonValueKind.Object ? Text(item, "severity") : null;
            var message = item.ValueKind == JsonValueKind.Object ? Text(item, "message")?.Trim() : null;
            var steps = item.ValueKind == JsonValueKind.Object ? Text(item, "steps")?.Trim() : null;
            if (severity is null || !Severities.Contains(severity) || string.IsNullOrEmpty(message) || message.Length > 500 || string.IsNullOrEmpty(steps) || steps.Length > 500)
            {
                error = $"\"findings\" item {number} needs a \"severity\" ({string.Join(", ", Severities)}), a \"message\" and \"steps\" that show it, each at most 500 characters";
                return false;
            }
            findings.Add(new TesterFinding(severity, message, steps));
        }
        return true;
    }

    private static bool ReadAssessments(JsonElement root, out List<FailureAssessment> assessments, out string error)
    {
        assessments = [];
        error = "";
        if (!root.TryGetProperty("assessments", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxAssessments)
        {
            error = $"\"assessments\" must be an array of at most {MaxAssessments} objects with \"test\", \"verdict\" and \"explanation\" (empty when nothing failed)";
            return false;
        }
        var number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            var test = item.ValueKind == JsonValueKind.Object ? Text(item, "test")?.Trim() : null;
            var verdict = item.ValueKind == JsonValueKind.Object ? Text(item, "verdict") : null;
            var explanation = item.ValueKind == JsonValueKind.Object ? Text(item, "explanation")?.Trim() : null;
            if (string.IsNullOrEmpty(test) || test.Length > 200 || verdict is not (TesterReport.ApplicationBug or TesterReport.TestProblem)
                || string.IsNullOrEmpty(explanation) || explanation.Length > 500)
            {
                error = $"\"assessments\" item {number} needs a \"test\" name, a \"verdict\" of application_bug or test_problem, and an \"explanation\" of at most 500 characters";
                return false;
            }
            assessments.Add(new FailureAssessment(test, verdict, explanation));
        }
        return true;
    }
}
