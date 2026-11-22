using System.Text.RegularExpressions;

namespace AiSdlcFactory.Contracts;

/// <summary>
/// The model every agent uses unless its role file names another (<c>FACTORY_MODEL</c>, as OpenCode writes it: <c>provider/model</c>).
/// Empty means no override, and the default in <c>agents/opencode.json</c> applies. The launcher gives it to the sandboxes that call the model
/// and the controller records it, so both read it the same way here.
/// </summary>
public static partial class ModelNames
{
    public const string Variable = "FACTORY_MODEL";

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9._-]{0,40}/[A-Za-z0-9][A-Za-z0-9._:/@+-]{0,100}\z")]
    private static partial Regex Pattern();

    public static bool IsValid(string name) => Pattern().IsMatch(name);

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
