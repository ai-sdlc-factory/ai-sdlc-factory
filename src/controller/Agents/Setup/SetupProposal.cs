using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using static AiSdlcFactory.Controller.JsonFields;

namespace AiSdlcFactory.Controller;

/// <summary>
/// What the <c>configurer</c> proposed for a repository with no <c>.factory/project.json</c>. <see cref="ConfigJson"/> is the settings file it asked for, already
/// checked by the real settings parser (<see cref="Project"/> is what it parsed to); it is a proposal, and the factory still runs every check in it before anything is kept.
/// </summary>
public sealed record SetupProposal(string Verdict, string Summary, string? ConfigJson, ProjectConfig? Project, IReadOnlyDictionary<string, string> Reasons)
{
    public const string Configured = "configured";
    public const string CannotConfigure = "cannot_configure";

    /// <summary>
    /// The settings file without the checks named in <paramref name="dropped"/> (those that failed when they were run), written as the file a pull request would hold.
    /// Null when no check is left, because a settings file needs at least one.
    /// </summary>
    public string? FileWithout(IReadOnlySet<string> dropped)
    {
        if (ConfigJson is null || JsonNode.Parse(ConfigJson) is not JsonObject root || root["gates"] is not JsonArray gates)
        {
            return null;
        }
        for (var index = gates.Count - 1; index >= 0; index--)
        {
            if (gates[index]?["name"]?.GetValue<string>() is { } name && dropped.Contains(name))
            {
                gates.RemoveAt(index);
            }
        }
        return gates.Count == 0 ? null : SetupParser.Render(root);
    }
}

public static class SetupParser
{
    /// <summary>The sandbox images a proposal may name (the factory's standard ones; a proposal never picks a repository's own image).</summary>
    public static readonly string[] Images = ["dotnet", "python", "node", "dotnet-node", "python-node", "full"];

    /// <summary>The only settings a proposal may carry. The rest (limits, rules, record, mode, ui) are for people to add later.</summary>
    public static readonly string[] Keys = ["image", "gates", "verify", "reviewers", "protected_paths"];

    public const int MaxSummary = 600;
    public const int MaxReason = 400;
    private static readonly string[] ReasonKeys = ["image", "gates", "reviewers", "protected_paths", "verify"];

    public static bool TryParse(string? text, [NotNullWhen(true)] out SetupProposal? result, out string error) =>
        AnswerReader.TryRead<SetupProposal>(text, Validate, out result, out error);

    private static bool Validate(JsonElement root, [NotNullWhen(true)] out SetupProposal? result, out string error)
    {
        result = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (SetupProposal.Configured or SetupProposal.CannotConfigure))
        {
            error = "\"verdict\" must be one of configured, cannot_configure";
            return false;
        }
        var summary = Text(root, "summary")?.Trim();
        if (string.IsNullOrEmpty(summary) || summary.Length > MaxSummary)
        {
            error = $"\"summary\" must be a non-empty string of at most {MaxSummary} characters";
            return false;
        }
        if (verdict == SetupProposal.CannotConfigure)
        {
            result = new SetupProposal(verdict, summary, null, null, new Dictionary<string, string>());
            error = "";
            return true;
        }

        if (!root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            error = "\"config\" must be a JSON object with the settings (image, gates, ...)";
            return false;
        }
        var unknown = config.EnumerateObject().Select(property => property.Name).Where(name => !Keys.Contains(name)).ToList();
        if (unknown.Count > 0)
        {
            error = $"\"config\" may only have {string.Join(", ", Keys)}; remove {string.Join(", ", unknown.Select(name => $"\"{name}\""))}";
            return false;
        }
        var image = Text(config, "image");
        if (image is null || !Images.Contains(image))
        {
            error = $"\"config.image\" must be one of {string.Join(", ", Images)}";
            return false;
        }
        if (config.TryGetProperty("verify", out var verify) && verify.ValueKind == JsonValueKind.Object
            && verify.TryGetProperty("image", out var verifyImage) && (verifyImage.ValueKind != JsonValueKind.String || (verifyImage.GetString() is not "browser" && !Images.Contains(verifyImage.GetString()))))
        {
            error = $"\"config.verify.image\" must be browser or one of {string.Join(", ", Images)}";
            return false;
        }

        var json = Render(JsonNode.Parse(config.GetRawText())!);
        if (!ProjectConfigParser.TryParse(json, out var project, out var problem))
        {
            error = $"\"config\" is not a usable settings file: {problem}";
            return false;
        }

        var reasons = new Dictionary<string, string>();
        if (root.TryGetProperty("reasons", out var reasonsValue) && reasonsValue.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in ReasonKeys)
            {
                if (Text(reasonsValue, key)?.Trim() is { Length: > 0 } reason)
                {
                    if (reason.Length > MaxReason)
                    {
                        error = $"\"reasons.{key}\" is {reason.Length} characters, the limit is {MaxReason}";
                        return false;
                    }
                    reasons[key] = reason;
                }
            }
        }
        result = new SetupProposal(verdict, summary, json, project, reasons);
        error = "";
        return true;
    }

    /// <summary>The settings file as it is written: indented, with a closing newline.</summary>
    public static string Render(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
}
