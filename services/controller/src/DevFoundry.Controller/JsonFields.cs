using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>Small strict readers for the fields of an agent's JSON answer.</summary>
public static class JsonFields
{
    public static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool Flag(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var raw) || raw.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = raw.GetBoolean();
        return true;
    }

    /// <summary>
    /// An array of non-empty strings, each trimmed and within the limits. On failure <paramref name="error"/>
    /// names the field and the exact limit that was broken, because the model only learns from that message.
    /// </summary>
    public static bool TextList(
        JsonElement element, string name, out IReadOnlyList<string> items, out string error,
        int maxItems = 20, int maxLength = 500)
    {
        var list = new List<string>();
        items = list;
        error = "";
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            error = $"\"{name}\" must be an array of strings";
            return false;
        }
        if (array.GetArrayLength() > maxItems)
        {
            error = $"\"{name}\" has {array.GetArrayLength()} items, the limit is {maxItems}";
            return false;
        }
        var number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            if (item.ValueKind != JsonValueKind.String || item.GetString()?.Trim() is not { Length: > 0 } text)
            {
                error = $"\"{name}\" item {number} must be a non-empty string";
                return false;
            }
            if (text.Length > maxLength)
            {
                error = $"\"{name}\" item {number} is {text.Length} characters, the limit is {maxLength}";
                return false;
            }
            list.Add(text);
        }
        return true;
    }
}

/// <summary>
/// File paths named by a model. They end up in comments and in shell arguments, so only plain
/// relative paths are accepted.
/// </summary>
public static partial class SafePath
{
    [GeneratedRegex(@"\A[A-Za-z0-9._@+/-]{1,200}\z")]
    private static partial Regex Allowed();

    [GeneratedRegex(@":\d+(-\d+)?\z")]
    private static partial Regex LineSuffix();

    /// <summary>
    /// Strips an optional <c>:line</c> suffix; false for absolute paths, <c>..</c>, or odd characters.
    /// With <paramref name="allowDirectory"/>, one trailing slash is accepted and kept to mark a directory.
    /// </summary>
    public static bool TryNormalize(string raw, out string path, bool allowDirectory = false)
    {
        path = LineSuffix().Replace(raw.Trim(), "");
        var body = allowDirectory && path.EndsWith('/') ? path[..^1] : path;
        return Allowed().IsMatch(path) && !body.StartsWith('/') && !body.Split('/').Any(part => part is ".." or "." or "");
    }
}
