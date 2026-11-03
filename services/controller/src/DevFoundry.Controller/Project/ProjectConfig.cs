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
public sealed record ProjectConfig(string Image, IReadOnlyList<Gate> Gates, IReadOnlyList<string> Reviewers, UiConfig? Ui = null, bool Record = false);

public static partial class ProjectConfigParser
{
    public const int MaxGates = 8;

    /// <summary>Review roles a project may switch on. The quality reviewer is the default.</summary>
    public static readonly string[] KnownReviewers = ["reviewer", "security"];
    public const int DefaultTimeoutSeconds = 600;
    private const int MaxArguments = 32;
    private const int MaxArgumentLength = 500;

    private static bool TryReadUi(JsonElement value, [NotNullWhen(true)] out UiConfig? ui, out string error)
    {
        ui = null;
        error = "";
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("paths", out var pathsValue) || pathsValue.ValueKind != JsonValueKind.Array
            || pathsValue.GetArrayLength() is 0 or > 20)
        {
            error = "\"ui\" must be an object with \"paths\": 1 to 20 patterns for the UI files, such as [\"web/**\", \"*.css\"]";
            return false;
        }
        var paths = new List<string>();
        foreach (var entry in pathsValue.EnumerateArray())
        {
            var pattern = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
            if (pattern is null || !Globs.IsValidPattern(pattern))
            {
                error = $"ui path \"{(entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.ToString())}\" is not a pattern like web/** or *.css (letters, digits and . _ - @ + / * ?, no leading / and no ..)";
                return false;
            }
            paths.Add(pattern);
        }

        if (!TryReadThreshold(value, "medium", UiConfig.DefaultMedium, out var medium, out error)
            || !TryReadThreshold(value, "large", UiConfig.DefaultLarge, out var large, out error))
        {
            return false;
        }
        if (large.Files < medium.Files || large.Lines < medium.Lines)
        {
            error = "\"ui\": the \"large\" thresholds must not be smaller than the \"medium\" ones";
            return false;
        }
        ui = new UiConfig(paths, medium, large);
        return true;
    }

    private static bool TryReadThreshold(JsonElement ui, string name, UiThreshold fallback, [NotNullWhen(true)] out UiThreshold? threshold, out string error)
    {
        threshold = fallback;
        error = "";
        if (!ui.TryGetProperty(name, out var value))
        {
            return true;
        }
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Number || !files.TryGetInt32(out var fileCount) || fileCount is < 1 or > 10_000
            || !value.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Number || !lines.TryGetInt32(out var lineCount) || lineCount is < 1 or > 1_000_000)
        {
            error = $"\"ui.{name}\" must have \"files\" (1 to 10000) and \"lines\" (1 to 1000000); a change reaches {name} when either number is reached";
            return false;
        }
        threshold = new UiThreshold(fileCount, lineCount);
        return true;
    }

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
            var reviewers = new List<string> { "reviewer" };
            if (root.TryGetProperty("reviewers", out var reviewersValue))
            {
                reviewers.Clear();
                if (reviewersValue.ValueKind != JsonValueKind.Array || reviewersValue.GetArrayLength() is 0 or > 4)
                {
                    error = $"\"reviewers\" must be an array of review roles: {string.Join(", ", KnownReviewers)}";
                    return false;
                }
                foreach (var entry in reviewersValue.EnumerateArray())
                {
                    var role = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
                    if (role is null || !KnownReviewers.Contains(role) || reviewers.Contains(role))
                    {
                        error = $"\"reviewers\" may list each of these once: {string.Join(", ", KnownReviewers)}";
                        return false;
                    }
                    reviewers.Add(role);
                }
            }
            UiConfig? ui = null;
            if (root.TryGetProperty("ui", out var uiValue) && !TryReadUi(uiValue, out ui, out error))
            {
                return false;
            }
            var record = false;
            if (root.TryGetProperty("record", out var recordValue))
            {
                if (recordValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "\"record\" must be true or false: true keeps what each agent was told and answered in .factory/runs/ on the pull request";
                    return false;
                }
                record = recordValue.GetBoolean();
            }
            config = new ProjectConfig(image, gates, reviewers, ui, record);
            return true;
        }
    }
}
