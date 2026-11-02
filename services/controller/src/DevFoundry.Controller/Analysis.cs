using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using static DevFoundry.Controller.JsonFields;

namespace DevFoundry.Controller;

public sealed record Complexity(
    int FilesEstimate, bool MultipleProjects, bool PublicApiChange, bool DataModelChange, bool NewDependency);

/// <summary>The analyst's answer, validated. The workflow routes on <see cref="Verdict"/> alone.</summary>
public sealed record AnalystVerdict(
    string Verdict,
    string Summary,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Questions,
    Complexity Complexity)
{
    public const string Pass = "pass";
    public const string NeedsClarification = "needs_clarification";
    public const string Reject = "reject";
}

/// <summary>
/// Turns model text into a verdict or a precise error. Models wrap JSON in prose or code fences and
/// occasionally break the shape, so this is strict about the shape and tolerant about the wrapping.
/// </summary>
public static class VerdictParser
{
    private const int MaxItems = 20;
    private const int MaxItemLength = 500;
    private const int MaxSummaryLength = 2000;

    public static bool TryParseAnalyst(string? text, [NotNullWhen(true)] out AnalystVerdict? verdict, out string error)
    {
        verdict = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "the reply was empty";
            return false;
        }

        // The last object that carries a verdict wins: models may quote an example before the answer.
        string? lastError = null;
        foreach (var candidate in JsonObjects.TopLevel(text).Reverse())
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
                if (Validate(document.RootElement, out verdict, out var problem))
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

    private static bool Validate(JsonElement root, [NotNullWhen(true)] out AnalystVerdict? verdict, out string error)
    {
        verdict = null;
        error = "";

        var verdictName = Text(root, "verdict");
        if (verdictName is not (AnalystVerdict.Pass or AnalystVerdict.NeedsClarification or AnalystVerdict.Reject))
        {
            error = "\"verdict\" must be one of pass, needs_clarification, reject";
            return false;
        }
        var summary = Text(root, "summary")?.Trim();
        if (string.IsNullOrEmpty(summary) || summary.Length > MaxSummaryLength)
        {
            error = $"\"summary\" must be a non-empty string of at most {MaxSummaryLength} characters";
            return false;
        }
        if (!TextList(root, "acceptance_criteria", out var criteria))
        {
            error = $"\"acceptance_criteria\" must be an array of at most {MaxItems} strings";
            return false;
        }
        if (!TextList(root, "questions", out var questions))
        {
            error = $"\"questions\" must be an array of at most {MaxItems} strings";
            return false;
        }
        if (!root.TryGetProperty("complexity", out var raw) || raw.ValueKind != JsonValueKind.Object
            || !raw.TryGetProperty("files_estimate", out var files) || files.ValueKind != JsonValueKind.Number
            || !files.TryGetInt32(out var filesEstimate)
            || filesEstimate is < 0 or > 10_000
            || !Flag(raw, "multiple_projects", out var multiple) || !Flag(raw, "public_api_change", out var api)
            || !Flag(raw, "data_model_change", out var data) || !Flag(raw, "new_dependency", out var dependency))
        {
            error = "\"complexity\" must have files_estimate (0 to 10000) and the booleans multiple_projects, public_api_change, data_model_change, new_dependency";
            return false;
        }

        if (verdictName == AnalystVerdict.Pass && (criteria.Count == 0 || questions.Count > 0))
        {
            error = "a pass needs at least one acceptance criterion and no open questions";
            return false;
        }
        if (verdictName == AnalystVerdict.NeedsClarification && questions.Count == 0)
        {
            error = "needs_clarification needs at least one question";
            return false;
        }

        verdict = new AnalystVerdict(
            verdictName, summary, criteria, questions,
            new Complexity(filesEstimate, multiple, api, data, dependency));
        return true;
    }

}
