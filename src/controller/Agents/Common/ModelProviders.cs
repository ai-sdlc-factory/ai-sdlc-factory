using System.Text.Json;

namespace AiSdlcFactory.Controller;

/// <summary>
/// Checks, when the factory starts, that the model a role was given in <c>FACTORY_ROLE_MODELS</c> is on a provider that <c>agents/opencode.json</c> enables (<c>enabled_providers</c>).
/// OpenCode does not use a provider that is not listed, so a role on one would fail every run with only "exited with code" on the ticket. Only what can be known is checked: where the file or its
/// list cannot be read, nothing is said, because the factory must not fail to start over an optional file, and the egress allowlist and the login are not looked at.
/// </summary>
public static class ModelProviders
{
    /// <summary>The providers <c>enabled_providers</c> lists in the agents folder, or null when there is no folder, no file, no such list or it cannot be read.</summary>
    public static IReadOnlyList<string>? Enabled(string? agentsDirectory)
    {
        if (string.IsNullOrWhiteSpace(agentsDirectory))
        {
            return null;
        }
        try
        {
            var file = $"{agentsDirectory.TrimEnd('/')}/opencode.json";
            if (!File.Exists(file))
            {
                return null;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("enabled_providers", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            return list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Stops the start with a message that names the role, the model and what to change, for the first role whose model is on a provider that is not enabled.</summary>
    public static void RequireEnabled(IReadOnlyDictionary<string, string> roleModels, string? agentsDirectory)
    {
        if (roleModels.Count == 0 || Enabled(agentsDirectory) is not { } enabled)
        {
            return;
        }
        foreach (var (role, model) in roleModels.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var provider = model[..model.IndexOf('/')];
            if (!enabled.Contains(provider, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{ModelNames.RolesVariable}: the model of \"{role}\" ({model}) is on the provider \"{provider}\", which agents/opencode.json does not enable (enabled_providers: {(enabled.Count == 0 ? "none" : string.Join(", ", enabled))}). " +
                    "Add it there, and make sure the sandboxes can reach it and have its login or key (docs/opencode-login.md#other-ways-to-sign-in).");
            }
        }
    }
}
