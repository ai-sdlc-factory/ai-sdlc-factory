using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>One automated check a change must pass: a command run in the sandbox, without model access.</summary>
public sealed record Gate(string Name, IReadOnlyList<string> Command, int TimeoutSeconds);

/// <summary>
/// What a repository tells the factory about itself, from <c>.factory/project.json</c>. It is untrusted
/// input: it can only pick among options the factory already offers (an image alias the launcher
/// allows) and name commands that run inside the same sandbox the agent already works in.
/// </summary>
public sealed record ProjectConfig(string Image, IReadOnlyList<Gate> Gates);

public static partial class ProjectConfigParser
{
    public const int MaxGates = 8;
    public const int DefaultTimeoutSeconds = 600;
    private const int MaxArguments = 32;
    private const int MaxArgumentLength = 500;

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,30}\z")]
    private static partial Regex ImageAlias();

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,29}\z")]
    private static partial Regex GateName();

    public static bool TryParse(string? json, [NotNullWhen(true)] out ProjectConfig? config, out string error)
    {
        config = null;
        error = "";
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json ?? "", new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            error = "the file is not valid JSON";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the file must contain a JSON object";
                return false;
            }
            var image = root.TryGetProperty("image", out var imageValue) && imageValue.ValueKind == JsonValueKind.String ? imageValue.GetString() : null;
            if (image is null || !ImageAlias().IsMatch(image))
            {
                error = "\"image\" must be the name of a sandbox image, such as \"dotnet\" or \"python\"";
                return false;
            }
            if (!root.TryGetProperty("gates", out var gatesValue) || gatesValue.ValueKind != JsonValueKind.Array
                || gatesValue.GetArrayLength() is 0 or > MaxGates)
            {
                error = $"\"gates\" must be an array of 1 to {MaxGates} checks";
                return false;
            }

            var gates = new List<Gate>();
            foreach (var item in gatesValue.EnumerateArray())
            {
                var number = gates.Count + 1;
                var name = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (name is null || !GateName().IsMatch(name) || gates.Any(g => g.Name == name))
                {
                    error = $"gate {number}: \"name\" must be unique, lower case, and use only letters, digits, - and _";
                    return false;
                }
                if (!item.TryGetProperty("command", out var commandValue) || commandValue.ValueKind != JsonValueKind.Array
                    || commandValue.GetArrayLength() is 0 or > MaxArguments)
                {
                    error = $"gate \"{name}\": \"command\" must be an array of 1 to {MaxArguments} strings, such as [\"python\", \"-m\", \"pytest\"]";
                    return false;
                }
                var command = new List<string>();
                foreach (var argument in commandValue.EnumerateArray())
                {
                    if (argument.ValueKind != JsonValueKind.String || argument.GetString() is not { Length: > 0 } text
                        || text.Length > MaxArgumentLength || text.Contains('\0'))
                    {
                        error = $"gate \"{name}\": every command argument must be a non-empty string of at most {MaxArgumentLength} characters";
                        return false;
                    }
                    command.Add(text);
                }
                var timeout = DefaultTimeoutSeconds;
                if (item.TryGetProperty("timeout_seconds", out var t) && (t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out timeout) || timeout is < 10 or > 1800))
                {
                    error = $"gate \"{name}\": \"timeout_seconds\" must be between 10 and 1800";
                    return false;
                }
                gates.Add(new Gate(name, command, timeout));
            }
            config = new ProjectConfig(image, gates);
            return true;
        }
    }
}
