using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSdlcFactory.Controller;

/// <summary>One automated check a change must pass: a command run in the sandbox, without model access.</summary>
public sealed record Gate(string Name, IReadOnlyList<string> Command, int TimeoutSeconds, string? WorkingDirectory = null)
{
    /// <summary>The command as people and agents read it, with the folder it runs in when it is not the repository root.</summary>
    public string Display => WorkingDirectory is null ? string.Join(' ', Command) : $"(in {WorkingDirectory}) {string.Join(' ', Command)}";

    /// <summary>
    /// What the sandbox is asked to run. A gate in a subfolder is wrapped in a tiny shell that changes into it first and then
    /// replaces itself with the command, so the command still runs without a shell of its own and its exit code is the gate's.
    /// </summary>
    public IReadOnlyList<string> SandboxCommand => WorkingDirectory is null
        ? Command
        : ["sh", "-c", "cd \"$1\" && shift && exec \"$@\"", "gate", WorkingDirectory, .. Command];
}

/// <summary>
/// What a repository tells the factory about itself, from <c>.factory/project.json</c>. It is untrusted
/// input: it can only pick among options the factory already offers (an image alias the launcher
/// allows) and name commands that run inside the same sandbox the agent already works in.
/// </summary>
public sealed record ProjectConfig(string Image, IReadOnlyList<Gate> Gates, IReadOnlyList<string> Reviewers, UiConfig? Ui = null, bool Record = false, bool QuickByDefault = false, IReadOnlyList<ProjectRule>? Rules = null,
    ProjectLimits? Limits = null, IReadOnlyList<string>? ProtectedPaths = null, VerifyConfig? Verify = null,
    IReadOnlyList<string>? Warnings = null, bool ConfirmCriteria = false);

public static partial class ProjectConfigParser
{
    public const int MaxGates = 8;

    /// <summary>Review roles a project may switch on. The quality reviewer is the default.</summary>
    public static readonly string[] KnownReviewers = ["reviewer", "security"];
    public const int DefaultTimeoutSeconds = 600;
    private const int MaxArguments = 32;
    private const int MaxArgumentLength = 500;

    private static bool TryReadUi(JsonElement root, out UiConfig? ui, out string error)
    {
        ui = null;
        error = "";
        if (!root.TryGetProperty("ui", out var value))
        {
            return true;
        }
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

    private static bool TryReadLimits(JsonElement root, out ProjectLimits? limits, out string error)
    {
        limits = null;
        error = "";
        if (!root.TryGetProperty("limits", out var value))
        {
            return true;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = "\"limits\" must be an object, for example {\"build_rounds\": 2}";
            return false;
        }
        int? buildRounds = null, clarificationRounds = null, revisions = null, tokenBudget = null, confirmOver = null, maxFiles = null;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name == "token_budget")
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var budget) || !LimitRanges.TokenBudgetOk(budget))
                {
                    error = $"\"limits.token_budget\" must be a whole number of tokens from {LimitRanges.MinTokenBudget:N0} to {LimitRanges.MaxTokenBudget:N0}";
                    return false;
                }
                tokenBudget = budget;
                continue;
            }
            if (property.Name == "confirm_over")
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var confirm) || !LimitRanges.TokenBudgetOk(confirm))
                {
                    error = $"\"limits.confirm_over\" must be a whole number of tokens from {LimitRanges.MinTokenBudget:N0} to {LimitRanges.MaxTokenBudget:N0}";
                    return false;
                }
                confirmOver = confirm;
                continue;
            }
            if (property.Name == "max_files")
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var files) || !LimitRanges.MaxFilesOk(files))
                {
                    error = $"\"limits.max_files\" must be a whole number of files from {LimitRanges.MinMaxFiles} to {LimitRanges.MaxMaxFiles:N0}: a feature the analyst estimates to affect more is sent back to be split";
                    return false;
                }
                maxFiles = files;
                continue;
            }
            var (name, ok, low, high) = property.Name switch
            {
                "build_rounds" => ("build_rounds", true, LimitRanges.MinBuildRounds, LimitRanges.MaxBuildRounds),
                "clarification_rounds" => ("clarification_rounds", true, LimitRanges.MinClarificationRounds, LimitRanges.MaxClarificationRounds),
                "revisions_per_pull_request" => ("revisions_per_pull_request", true, LimitRanges.MinRevisions, LimitRanges.MaxRevisions),
                _ => (property.Name, false, 0, 0),
            };
            if (!ok)
            {
                error = $"\"limits\" has no setting \"{property.Name}\"; the settings are build_rounds, clarification_rounds, revisions_per_pull_request, token_budget, confirm_over and max_files";
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
        limits = new ProjectLimits(buildRounds, clarificationRounds, revisions, tokenBudget, confirmOver, maxFiles);
        return true;
    }

    private static bool TryReadProtectedPaths(JsonElement root, out List<string> paths, out string error)
    {
        paths = [];
        error = "";
        if (!root.TryGetProperty("protected_paths", out var value))
        {
            return true;
        }
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

    private static bool TryReadRules(JsonElement root, out List<ProjectRule> rules, out string error)
    {
        rules = [];
        error = "";
        if (!root.TryGetProperty("rules", out var value))
        {
            return true;
        }
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

    [GeneratedRegex(@"\A[A-Za-z0-9._@+/-]{1,100}\z")]
    private static partial Regex GateDirectory();

    /// <summary>Reads <c>.factory/project.json</c>. Returns false with a message that names the problem, written for the person who edits the file.</summary>
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
            // One reader per key, in the order the reference in docs/project-setup.md lists them; the first problem found is the one reported.
            if (!TryReadImage(root, out var image, out error)
                || !TryReadGates(root, out var gates, out error)
                || !TryReadReviewers(root, out var reviewers, out error)
                || !TryReadUi(root, out var ui, out error)
                || !TryReadRecord(root, out var record, out error)
                || !TryReadMode(root, out var quickByDefault, out error)
                || !TryReadRules(root, out var rules, out error)
                || !TryReadLimits(root, out var limits, out error)
                || !TryReadProtectedPaths(root, out var protectedPaths, out error)
                || !TryReadVerify(root, out var verify, out error)
                || !TryReadConfirmCriteria(root, out var confirmCriteria, out error))
            {
                return false;
            }
            config = new ProjectConfig(image, gates, reviewers, ui, record, quickByDefault, rules, limits, protectedPaths, verify, ProjectKeys.Warnings(root), confirmCriteria);
            return true;
        }
    }

    private static bool TryReadImage(JsonElement root, out string image, out string error)
    {
        image = "";
        error = "";
        var value = root.TryGetProperty("image", out var imageValue) && imageValue.ValueKind == JsonValueKind.String ? imageValue.GetString() : null;
        if (value is null || !ImageAlias().IsMatch(value))
        {
            error = "\"image\" must be the name of a sandbox image, such as \"dotnet\" or \"python\"";
            return false;
        }
        image = value;
        return true;
    }

    private static bool TryReadGates(JsonElement root, out List<Gate> gates, out string error)
    {
        gates = [];
        error = "";
        if (!root.TryGetProperty("gates", out var gatesValue) || gatesValue.ValueKind != JsonValueKind.Array
            || gatesValue.GetArrayLength() is 0 or > MaxGates)
        {
            error = $"\"gates\" must be an array of 1 to {MaxGates} checks";
            return false;
        }
        foreach (var item in gatesValue.EnumerateArray())
        {
            if (!TryReadGate(item, gates, out var gate, out error))
            {
                return false;
            }
            gates.Add(gate);
        }
        return true;
    }

    /// <summary>One check. <paramref name="earlier"/> are the checks read before it, so a name can be required to be unique.</summary>
    private static bool TryReadGate(JsonElement item, IReadOnlyList<Gate> earlier, [NotNullWhen(true)] out Gate? gate, out string error)
    {
        gate = null;
        error = "";
        var number = earlier.Count + 1;
        var name = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (name is null || !GateName().IsMatch(name) || earlier.Any(g => g.Name == name))
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
        string? workingDirectory = null;
        if (item.TryGetProperty("cwd", out var cwdValue))
        {
            workingDirectory = cwdValue.ValueKind == JsonValueKind.String ? cwdValue.GetString()?.TrimEnd('/') : null;
            if (workingDirectory is null || !GateDirectory().IsMatch(workingDirectory) || workingDirectory.Contains("..") || workingDirectory.StartsWith('/'))
            {
                error = $"gate \"{name}\": \"cwd\" must be a folder inside the repository, such as \"frontend\" (letters, digits and . _ - @ + /, no leading / and no ..)";
                return false;
            }
        }
        gate = new Gate(name, command, timeout, workingDirectory);
        return true;
    }

    private static bool TryReadReviewers(JsonElement root, out List<string> reviewers, out string error)
    {
        reviewers = ["reviewer"];
        error = "";
        if (!root.TryGetProperty("reviewers", out var value))
        {
            return true;
        }
        reviewers.Clear();
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > 4)
        {
            error = $"\"reviewers\" must be an array of review roles: {string.Join(", ", KnownReviewers)}";
            return false;
        }
        foreach (var entry in value.EnumerateArray())
        {
            var role = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
            if (role is null || !KnownReviewers.Contains(role) || reviewers.Contains(role))
            {
                error = $"\"reviewers\" may list each of these once: {string.Join(", ", KnownReviewers)}";
                return false;
            }
            reviewers.Add(role);
        }
        return true;
    }

    private static bool TryReadRecord(JsonElement root, out bool record, out string error)
    {
        record = false;
        error = "";
        if (!root.TryGetProperty("record", out var value))
        {
            return true;
        }
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = "\"record\" must be true or false: true keeps what each agent was told and answered in .factory/runs/ on the pull request";
            return false;
        }
        record = value.GetBoolean();
        return true;
    }

    private static bool TryReadConfirmCriteria(JsonElement root, out bool confirmCriteria, out string error)
    {
        confirmCriteria = false;
        error = "";
        if (!root.TryGetProperty("confirm_criteria", out var value))
        {
            return true;
        }
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = "\"confirm_criteria\" must be true or false: true makes a feature run wait for a person's /approve of the analyst's acceptance criteria before anything is built";
            return false;
        }
        confirmCriteria = value.GetBoolean();
        return true;
    }

    private static bool TryReadMode(JsonElement root, out bool quickByDefault, out string error)
    {
        quickByDefault = false;
        error = "";
        if (!root.TryGetProperty("mode", out var value))
        {
            return true;
        }
        var mode = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (mode is not ("full" or "quick"))
        {
            error = "\"mode\" must be \"full\" (the default) or \"quick\": quick mode skips the coverage check, the design review and the acceptance check for tickets in this project";
            return false;
        }
        quickByDefault = mode == "quick";
        return true;
    }
}
