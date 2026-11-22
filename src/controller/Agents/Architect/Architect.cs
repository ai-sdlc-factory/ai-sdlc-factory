using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using static AiSdlcFactory.Controller.JsonFields;

namespace AiSdlcFactory.Controller;

public sealed record ComponentChange(string Path, string Change);

/// <summary>The architect's validated design. The workflow routes on <see cref="Verdict"/> alone.</summary>
public sealed record ArchitectDesign(
    string Verdict,
    string Summary,
    string Approach,
    IReadOnlyList<ComponentChange> Components,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Questions,
    IReadOnlyList<string> Guidance)
{
    public const string Pass = "pass";
    public const string NeedsDecision = "needs_decision";
    public const string Reject = "reject";
}

public static class ArchitectParser
{
    public const int MaxText = 2000;
    public const int MaxComponents = 30;
    public const int MaxChange = 500;
    public const int MaxRisks = 10;
    public const int MaxGuidance = 15;
    public const int MaxQuestions = 3;

    public static bool TryParse(string? text, [NotNullWhen(true)] out ArchitectDesign? design, out string error)
    {
        design = null;
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
                if (Validate(document.RootElement, out design, out var problem))
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

    private static bool Validate(JsonElement root, [NotNullWhen(true)] out ArchitectDesign? design, out string error)
    {
        design = null;
        error = "";

        var verdict = Text(root, "verdict");
        if (verdict is not (ArchitectDesign.Pass or ArchitectDesign.NeedsDecision or ArchitectDesign.Reject))
        {
            error = "\"verdict\" must be one of pass, needs_decision, reject";
            return false;
        }
        var summary = Text(root, "summary")?.Trim();
        if (string.IsNullOrEmpty(summary) || summary.Length > MaxText)
        {
            error = $"\"summary\" must be a non-empty string of at most {MaxText} characters";
            return false;
        }
        var approach = Text(root, "approach")?.Trim() ?? "";
        if (approach.Length > MaxText)
        {
            error = $"\"approach\" must be at most {MaxText} characters";
            return false;
        }
        if (!Components(root, out var components, out error)
            || !TextList(root, "risks", out var risks, out error, MaxRisks)
            || !TextList(root, "guidance", out var guidance, out error, MaxGuidance)
            || !TextList(root, "questions", out var questions, out error, MaxQuestions))
        {
            return false;
        }

        if (verdict == ArchitectDesign.Pass && (approach.Length == 0 || components.Count == 0 || questions.Count > 0))
        {
            error = "a pass needs an \"approach\", at least one component, and no open questions";
            return false;
        }
        if (verdict == ArchitectDesign.NeedsDecision && questions.Count == 0)
        {
            error = "needs_decision needs at least one question";
            return false;
        }

        design = new ArchitectDesign(verdict, summary, approach, components, risks, questions, guidance);
        return true;
    }

    private static bool Components(JsonElement root, out IReadOnlyList<ComponentChange> components, out string error)
    {
        var list = new List<ComponentChange>();
        components = list;
        error = "";
        if (!root.TryGetProperty("components", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            error = "\"components\" must be an array";
            return false;
        }
        if (array.GetArrayLength() > MaxComponents)
        {
            error = $"\"components\" has {array.GetArrayLength()} entries, the limit is {MaxComponents}; group related files into one directory entry";
            return false;
        }
        var number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            var path = item.ValueKind == JsonValueKind.Object ? Text(item, "path") : null;
            var change = item.ValueKind == JsonValueKind.Object ? Text(item, "change")?.Trim() : null;
            if (path is null || string.IsNullOrEmpty(change))
            {
                error = $"component {number} needs a \"path\" and a non-empty \"change\", both strings";
                return false;
            }
            if (!SafePath.TryNormalize(path, out var normal, allowDirectory: true))
            {
                error = $"component {number}: \"{(path.Length > 60 ? path[..60] : path)}\" is not a plain relative path (no leading /, no .., only letters, digits and . _ - @ + /)";
                return false;
            }
            if (change.Length > MaxChange)
            {
                error = $"component {number}: \"change\" is {change.Length} characters, the limit is {MaxChange}";
                return false;
            }
            list.Add(new ComponentChange(normal, change));
        }
        return true;
    }
}
