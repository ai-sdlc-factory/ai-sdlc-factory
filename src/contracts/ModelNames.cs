using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSdlcFactory.Contracts;

/// <summary>
/// The model every agent uses unless its role file names another (<c>FACTORY_MODEL</c>, as OpenCode writes it: <c>provider/model</c>).
/// Empty means no override, and the default in <c>agents/opencode.json</c> applies. The launcher gives it to the sandboxes that call the model
/// and the controller records it, so both read it the same way here.
/// <c>FACTORY_ROLE_MODELS</c> is the same choice for single roles, set by the operator without editing a role file: a JSON object from a role's name to its model. It ranks first:
/// the role's entry there, else the role file's <c>model:</c> line, else <c>FACTORY_MODEL</c>, else the default in <c>opencode.json</c>.
/// </summary>
public static partial class ModelNames
{
    public const string Variable = "FACTORY_MODEL";
    public const string RolesVariable = "FACTORY_ROLE_MODELS";

    /// <summary>The most roles a setting may name; there are fewer than 30 roles, so more than this is a mistake.</summary>
    public const int MaxRoleModels = 40;

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,31}\z")]
    private static partial Regex RolePattern();

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9._-]{0,40}/[A-Za-z0-9][A-Za-z0-9._:/@+-]{0,100}\z")]
    private static partial Regex Pattern();

    public static bool IsValid(string name) => Pattern().IsMatch(name);

    /// <summary>
    /// <c>FACTORY_ROLE_MODELS</c>: a JSON object such as <c>{"reviewer":"openai/gpt-6.1-sol","security":"openai/gpt-6.1-sol-fast"}</c>. Empty means no role has a model of its own here.
    /// Anything else (not an object, a role name that is not a role's shape, a model that is not <c>provider/model</c>) stops the start with a message, because a typo would otherwise fail every run of that role.
    /// Whether a name is one of the factory's roles is checked by the controller, which knows them.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadRoles(string? value)
    {
        var models = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return models;
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{RolesVariable} must be a JSON object from a role to its model, for example {{\"reviewer\":\"openai/gpt-6.1-sol\"}}, but it is not valid JSON.");
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"{RolesVariable} must be a JSON object from a role to its model, for example {{\"reviewer\":\"openai/gpt-6.1-sol\"}}.");
            }
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (models.Count >= MaxRoleModels)
                {
                    throw new InvalidOperationException($"{RolesVariable} names more than {MaxRoleModels} roles.");
                }
                if (!RolePattern().IsMatch(entry.Name))
                {
                    throw new InvalidOperationException($"{RolesVariable}: \"{Short(entry.Name)}\" is not a role name (lower case letters, digits, '-' and '_', at most 32).");
                }
                var model = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString()!.Trim() : "";
                if (!IsValid(model))
                {
                    throw new InvalidOperationException($"{RolesVariable}: the model of \"{entry.Name}\" must look like provider/model (for example openai/gpt-6.1-sol).");
                }
                models[entry.Name] = model;
            }
        }
        return models;
    }

    private static string Short(string text) => text.Length <= 40 ? text : text[..40] + "...";

    /// <summary>The setting's value, or null when it is empty. Anything that is not <c>provider/model</c> stops the start with a message, because a typo would otherwise fail every run.</summary>
    public static string? Read(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        value = value.Trim();
        return IsValid(value)
            ? value
            : throw new InvalidOperationException($"{Variable} must look like provider/model (for example openai/gpt-6.1-sol), but it is \"{value}\".");
    }
}
