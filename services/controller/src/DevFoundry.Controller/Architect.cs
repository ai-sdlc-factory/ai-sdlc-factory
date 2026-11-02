using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using static DevFoundry.Controller.JsonFields;

namespace DevFoundry.Controller;

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
    private const int MaxText = 2000;

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
        if (!Components(root, out var components))
        {
            error = "\"components\" must be an array of at most 20 entries, each with a relative file or directory \"path\" and a \"change\" of at most 300 characters";
            return false;
        }
        if (!TextList(root, "risks", out var risks, maxItems: 10) || !TextList(root, "guidance", out var guidance, maxItems: 10)
            || !TextList(root, "questions", out var questions, maxItems: 3))
        {
            error = "\"risks\" and \"guidance\" must be arrays of at most 10 strings and \"questions\" an array of at most 3 strings, each up to 500 characters";
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

    private static bool Components(JsonElement root, out IReadOnlyList<ComponentChange> components)
    {
        var list = new List<ComponentChange>();
        components = list;
        if (!root.TryGetProperty("components", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 20)
        {
            return false;
        }
        foreach (var item in array.EnumerateArray())
        {
            var path = item.ValueKind == JsonValueKind.Object ? Text(item, "path") : null;
            var change = item.ValueKind == JsonValueKind.Object ? Text(item, "change")?.Trim() : null;
            if (path is null || !SafePath.TryNormalize(path, out var normal)
                || string.IsNullOrEmpty(change) || change.Length > 300)
            {
                return false;
            }
            list.Add(new ComponentChange(normal, change));
        }
        return true;
    }
}
