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
public sealed record ProjectConfig(string Image, IReadOnlyList<Gate> Gates, IReadOnlyList<string> Reviewers, UiConfig? Ui = null, bool Record = false, bool QuickByDefault = false, IReadOnlyList<ProjectRule>? Rules = null,
    ProjectLimits? Limits = null, IReadOnlyList<string>? ProtectedPaths = null);

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

    public const int MaxProtectedPaths = 20;

    private static bool TryReadLimits(JsonElement value, out ProjectLimits limits, out string error)
    {
        limits = new ProjectLimits();
        error = "";
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = "\"limits\" must be an object, for example {\"build_rounds\": 2}";
            return false;
        }
        int? buildRounds = null, clarificationRounds = null, revisions = null;
        foreach (var property in value.EnumerateObject())
        {
            var (name, ok, low, high) = property.Name switch
            {
                "build_rounds" => ("build_rounds", true, LimitRanges.MinBuildRounds, LimitRanges.MaxBuildRounds),
                "clarification_rounds" => ("clarification_rounds", true, LimitRanges.MinClarificationRounds, LimitRanges.MaxClarificationRounds),
                "revisions_per_pull_request" => ("revisions_per_pull_request", true, LimitRanges.MinRevisions, LimitRanges.MaxRevisions),
                _ => (property.Name, false, 0, 0),
            };
            if (!ok)
            {
                error = $"\"limits\" has no setting \"{property.Name}\"; the settings are build_rounds, clarification_rounds and revisions_per_pull_request";
                return false;
            }
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number) || number < low || number > high)
            {
                error = $"\"limits.{name}\" must be a whole number from {low} to {high}";
                return false;
            }
            switch (name)
            {
                case "build_rounds": buildRounds = number; break;
                case "clarification_rounds": clarificationRounds = number; break;
                default: revisions = number; break;
            }
        }
        limits = new ProjectLimits(buildRounds, clarificationRounds, revisions);
        return true;
    }

    private static bool TryReadProtectedPaths(JsonElement value, out List<string> paths, out string error)
    {
        paths = [];
        error = "";
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxProtectedPaths)
        {
            error = $"\"protected_paths\" must be an array of at most {MaxProtectedPaths} patterns, such as [\"db/migrations/**\", \"*.lock\"]";
            return false;
        }
        foreach (var entry in value.EnumerateArray())
        {
            var pattern = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
            if (pattern is null || !Globs.IsValidPattern(pattern))
            {
                error = $"protected path \"{(entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.ToString())}\" is not a pattern like db/migrations/** or *.lock (letters, digits and . _ - @ + / * ?, no leading / and no ..)";
                return false;
            }
            if (!paths.Contains(pattern))
            {
                paths.Add(pattern);
            }
        }
        return true;
    }

    private static bool TryReadRules(JsonElement value, out List<ProjectRule> rules, out string error)
    {
        rules = [];
        error = "";
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > ProjectRules.MaxRules)
        {
            error = $"\"rules\" must be an array of at most {ProjectRules.MaxRules} rules, each with \"id\", \"files\", \"forbid\" and \"message\"";
            return false;
        }
        foreach (var item in value.EnumerateArray())
        {
            var id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
            if (id is null || !GateName().IsMatch(id) || rules.Any(r => r.Id == id))
            {
                error = $"rule {rules.Count + 1}: \"id\" must be unique, lower case, and use only letters, digits, - and _";
                return false;
            }
            var files = new List<string>();
            if (item.TryGetProperty("files", out var filesValue))
            {
                if (filesValue.ValueKind == JsonValueKind.String)
                {
                    files.Add(filesValue.GetString() ?? "");
                }
                else if (filesValue.ValueKind == JsonValueKind.Array && filesValue.GetArrayLength() is >= 1 and <= 5)
                {
                    files.AddRange(filesValue.EnumerateArray().Select(f => f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : ""));
                }
            }
            if (files.Count == 0 || files.Any(f => !Globs.IsValidPattern(f)))
            {
                error = $"rule \"{id}\": \"files\" must be a pattern like src/**/*.cs, or 1 to 5 of them (letters, digits and . _ - @ + / * ?, no leading / and no ..)";
                return false;
            }
            var forbid = item.TryGetProperty("forbid", out var forbidValue) && forbidValue.ValueKind == JsonValueKind.String ? forbidValue.GetString() : null;
            if (string.IsNullOrEmpty(forbid) || forbid.Length > ProjectRules.MaxPatternLength)
            {
                error = $"rule \"{id}\": \"forbid\" must be a regular expression of at most {ProjectRules.MaxPatternLength} characters; a line a change adds that matches it breaks the rule";
                return false;
            }
            if (ProjectRules.Compile(forbid, out var regexError) is null)
            {
                error = $"rule \"{id}\": \"forbid\" {regexError}";
                return false;
            }
            var message = item.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String ? messageValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(message) || message.Length > ProjectRules.MaxMessageLength || message.Contains('\0'))
            {
                error = $"rule \"{id}\": \"message\" must say, in at most {ProjectRules.MaxMessageLength} characters, what to do instead";
                return false;
            }
            var block = true;
            if (item.TryGetProperty("severity", out var severityValue))
            {
                var severity = severityValue.ValueKind == JsonValueKind.String ? severityValue.GetString() : null;
                if (severity is not ("block" or "warn"))
                {
                    error = $"rule \"{id}\": \"severity\" must be \"block\" (the default: the developer must fix it) or \"warn\" (only the reviewers are told)";
                    return false;
                }
                block = severity == "block";
            }
            rules.Add(new ProjectRule(id, files, forbid, message.Trim(), block));
        }
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
            var quickByDefault = false;
            if (root.TryGetProperty("mode", out var modeValue))
            {
                var mode = modeValue.ValueKind == JsonValueKind.String ? modeValue.GetString() : null;
                if (mode is not ("full" or "quick"))
                {
                    error = "\"mode\" must be \"full\" (the default) or \"quick\": quick mode skips the coverage check, the design review and QA for tickets in this project";
                    return false;
                }
                quickByDefault = mode == "quick";
            }
            var rules = new List<ProjectRule>();
            if (root.TryGetProperty("rules", out var rulesValue) && !TryReadRules(rulesValue, out rules, out error))
            {
                return false;
            }
            ProjectLimits? limits = null;
            if (root.TryGetProperty("limits", out var limitsValue))
            {
                if (!TryReadLimits(limitsValue, out var readLimits, out error))
                {
                    return false;
                }
                limits = readLimits;
            }
            var protectedPaths = new List<string>();
            if (root.TryGetProperty("protected_paths", out var protectedValue) && !TryReadProtectedPaths(protectedValue, out protectedPaths, out error))
            {
                return false;
            }
            config = new ProjectConfig(image, gates, reviewers, ui, record, quickByDefault, rules, limits, protectedPaths);
            return true;
        }
    }
}
