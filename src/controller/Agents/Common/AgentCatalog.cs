using System.Text.Json;

namespace AiSdlcFactory.Controller;

/// <summary>
/// Looks up which model a role is configured to use, from the same agent folder the sandboxes get. OpenCode does
/// not say which model answered, so this is the configured model: the role file's <c>model:</c> line, else <c>FACTORY_MODEL</c>,
/// else the default in <c>opencode.json</c>.
/// </summary>
public static class AgentCatalog
{
    public static string? ModelFor(string? directory, string role, string? factoryModel = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !RunRecord.IsValidSlug(role))
        {
            return null;
        }
        try
        {
            var file = $"{directory.TrimEnd('/')}/agents/{role}.md";
            if (File.Exists(file) && FrontMatterModel(File.ReadLines(file)) is { } fromRole)
            {
                return fromRole;
            }
            if (!string.IsNullOrWhiteSpace(factoryModel))
            {
                return factoryModel;
            }
            var config = $"{directory.TrimEnd('/')}/opencode.json";
            if (File.Exists(config))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(config), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (document.RootElement.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                {
                    return model.GetString();
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // The model name is a nicety for the record; never fail a run over it.
        }
        return null;
    }

    private static string? FrontMatterModel(IEnumerable<string> lines)
    {
        var first = true;
        foreach (var line in lines)
        {
            if (first)
            {
                if (line.Trim() != "---")
                {
                    return null;
                }
                first = false;
                continue;
            }
            if (line.Trim() == "---")
            {
                return null;
            }
            if (line.StartsWith("model:", StringComparison.Ordinal))
            {
                var value = line["model:".Length..].Trim().Trim('"', '\'');
                return value.Length is > 0 and <= 100 ? value : null;
            }
        }
        return null;
    }
}
