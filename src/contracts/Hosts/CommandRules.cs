using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSdlcFactory.Contracts;

/// <summary>What a person must have on a repository to steer the factory with a comment (or a review). Ordered from the most open to the most closed.</summary>
public enum CommandLevel
{
    /// <summary>Anyone who can comment. The default, and how the factory always worked.</summary>
    Anyone,

    /// <summary>Someone with write access to the repository (on Forgejo and GitHub: write, maintain or admin).</summary>
    Write,

    /// <summary>Someone with admin access to the repository.</summary>
    Admin,

    /// <summary>Nobody by their access: only the people named in <see cref="CommandRule.Allow"/>.</summary>
    None,
}

/// <summary>
/// Who may steer the factory on a repository: use a slash command in a comment, answer an agent, or ask for a revision with a review. Set by the operator in <c>FACTORY_HOSTS</c>, never by the
/// repository. A login on <see cref="Deny"/> is always refused, then one on <see cref="Allow"/> always passes, then <see cref="Who"/> decides by what the host says the person may do.
/// Starting a run by a label is not covered: the host already limits who may label.
/// </summary>
public sealed partial record CommandRule(
    CommandLevel Who = CommandLevel.Anyone,
    IReadOnlySet<string>? Allow = null,
    IReadOnlySet<string>? Deny = null,
    bool Silent = false,
    IReadOnlyDictionary<string, CommandRule>? Each = null,
    bool Project = false)
{
    /// <summary>Everyone, as before there was a rule.</summary>
    public static readonly CommandRule Open = new();

    public IReadOnlySet<string> Allowed => Allow ?? EmptyNames;
    public IReadOnlySet<string> Denied => Deny ?? EmptyNames;

    private static readonly IReadOnlySet<string> EmptyNames = new HashSet<string>();

    /// <summary>The rules of single commands (<see cref="CommandNames"/>), each on top of this one: a person has to pass both.</summary>
    public IReadOnlyDictionary<string, CommandRule> EachCommand => Each ?? NoCommands;

    private static readonly IReadOnlyDictionary<string, CommandRule> NoCommands = new Dictionary<string, CommandRule>();

    /// <summary>True when nobody is held back and nothing has to be looked at: the lowest level, no one denied, no rule of a single command, and the repository may not add its own.</summary>
    public bool IsOpen => Who == CommandLevel.Anyone && Denied.Count == 0 && EachCommand.Count == 0 && !Project;

    /// <summary>This rule without the parts that belong to the whole (the rules of single commands, the project's say): what one command's own rule is.</summary>
    public bool HoldsBack => Who != CommandLevel.Anyone || Denied.Count > 0;

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._@-]{0,99}\z")]
    private static partial Regex LoginPattern();

    /// <summary>One line for a log, with no one's name: what the rule asks of a person.</summary>
    public string Describe() => (Who, HoldsBack) switch
    {
        (CommandLevel.Anyone, false) => "anyone who can comment may use commands",
        (CommandLevel.Anyone, true) => $"anyone who can comment may use commands, except {Denied.Count} named",
        (CommandLevel.Write, _) => $"commands need write access{Names()}",
        (CommandLevel.Admin, _) => $"commands need admin access{Names()}",
        _ => $"commands only for the {Allowed.Count} named" + (Denied.Count > 0 ? $" (and {Denied.Count} denied)" : ""),
    } + (EachCommand.Count > 0 ? $"; {EachCommand.Count} commands have a rule of their own" : "") + (Project ? "; the repository may tighten it" : "");

    private string Names() => (Allowed.Count, Denied.Count) switch
    {
        (0, 0) => "",
        (var allowed, 0) => $", and {allowed} named",
        (0, var denied) => $", {denied} named are refused",
        (var allowed, var denied) => $", {allowed} named are let in and {denied} refused",
    };

    /// <summary>
    /// Reads a <c>commands</c> setting: a level as text (<c>"write"</c>), or an object with <c>who</c> (a level), <c>allow</c> and <c>deny</c> (lists of logins) and <c>refusal</c> (<c>"comment"</c>, the default,
    /// or <c>"silent"</c>). A setting that cannot be used is an error that names where it is.
    /// </summary>
    public static CommandRule Read(JsonElement value, string where) => Read(value, where, nested: false);

    private static CommandRule Read(JsonElement value, string where, bool nested)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return new CommandRule(Level(value.GetString(), where));
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{where} must be a level (anyone, write, admin, none) or an object.");
        }
        var who = CommandLevel.Anyone;
        HashSet<string>? allow = null, deny = null;
        var silent = false;
        var project = false;
        JsonElement? each = null;
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "each" when !nested:
                    each = property.Value;
                    break;
                case "project" when !nested:
                    project = property.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw new InvalidOperationException($"{where}: project must be true or false."),
                    };
                    break;
                case "who":
                    who = Level(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null, $"{where}: who");
                    break;
                case "allow":
                    allow = Logins(property.Value, $"{where}: allow");
                    break;
                case "deny":
                    deny = Logins(property.Value, $"{where}: deny");
                    break;
                case "refusal":
                    var text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    silent = text switch
                    {
                        "silent" => true,
                        "comment" => false,
                        _ => throw new InvalidOperationException($"{where}: refusal must be \"comment\" or \"silent\"."),
                    };
                    break;
                default:
                    throw new InvalidOperationException($"{where}: unknown setting \"{Short(property.Name)}\" ({(nested ? "who, allow, deny, refusal" : "who, allow, deny, refusal, each, project")}).");
            }
        }
        return new CommandRule(who, allow, deny, silent, each is null ? null : Commands(each.Value, silent, $"{where}: each"), project);
    }

    private static Dictionary<string, CommandRule> Commands(JsonElement list, bool silent, string where)
    {
        if (list.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{where} must be an object of command names to rules.");
        }
        var rules = new Dictionary<string, CommandRule>();
        foreach (var command in list.EnumerateObject())
        {
            if (!CommandNames.All.Contains(command.Name))
            {
                throw new InvalidOperationException($"{where}: \"{Short(command.Name)}\" is not a command name ({string.Join(", ", CommandNames.All)}).");
            }
            var own = Read(command.Value, $"{where}.{command.Name}", nested: true);
            // A command's own rule refuses as quietly as the whole one unless it says otherwise by being louder: a public repository that is silent stays silent.
            rules[command.Name] = silent && !own.Silent ? own with { Silent = true } : own;
        }
        return rules;
    }

    private static CommandLevel Level(string? text, string where) => text switch
    {
        "anyone" => CommandLevel.Anyone,
        "write" => CommandLevel.Write,
        "admin" => CommandLevel.Admin,
        "none" => CommandLevel.None,
        _ => throw new InvalidOperationException($"{where} must be one of anyone, write, admin, none."),
    };

    internal static HashSet<string> ReadLogins(JsonElement list, string where) => Logins(list, where);

    private static HashSet<string> Logins(JsonElement list, string where)
    {
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 200)
        {
            throw new InvalidOperationException($"{where} must be a list of at most 200 logins.");
        }
        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list.EnumerateArray())
        {
            var login = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
            if (!LoginPattern().IsMatch(login))
            {
                throw new InvalidOperationException($"{where} has a login that cannot be used (letters, digits, . _ - and @, at most 100 characters).");
            }
            logins.Add(login);
        }
        return logins;
    }

    private static string Short(string value) => value.Length > 30 ? value[..30] + "..." : value;
}

/// <summary>
/// The names a rule can give to single commands: the slash commands (<c>fix</c> also stands for <c>/revise</c>, <c>option</c> for <c>/choose</c>), <c>reply</c> for an answer to an agent that is not a
/// command, <c>changes</c> for a review that asks for changes, and <c>factory</c> for <c>/factory &lt;kind&gt;</c>.
/// </summary>
public static class CommandNames
{
    public static readonly IReadOnlyList<string> All = ["ask", "setup", "retry", "fix", "update", "qa", "review", "factory", "approve", "option", "hotfix", "resolved", "reply", "changes"];
}

/// <summary>
/// What a repository's own <c>.factory/project.json</c> may say about who may steer the factory, where the operator allowed it (<see cref="CommandRule.Project"/>): only to tighten. A level
/// of write or admin, and people to refuse. It can never let anyone in, and a person the operator named in <c>allow</c> is not touched by it.
/// </summary>
public sealed record ProjectCommandRule(CommandLevel Who, IReadOnlySet<string> Deny)
{
    /// <summary>A setting that says nothing.</summary>
    public static readonly ProjectCommandRule None = new(CommandLevel.Anyone, new HashSet<string>());

    public bool Tightens => Who != CommandLevel.Anyone || Deny.Count > 0;

    /// <summary>Reads the <c>commands</c> key of a project file: <c>"write"</c> or <c>"admin"</c>, or an object with <c>who</c> and <c>deny</c>. Anything that could loosen (an <c>allow</c> list) is an error.</summary>
    public static ProjectCommandRule Read(JsonElement value, string where)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return new ProjectCommandRule(Level(value.GetString(), where), new HashSet<string>());
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{where} must be \"write\", \"admin\" or an object with who and deny.");
        }
        var who = CommandLevel.Anyone;
        var deny = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "who":
                    who = Level(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null, $"{where}: who");
                    break;
                case "deny":
                    foreach (var login in CommandRule.ReadLogins(property.Value, $"{where}: deny"))
                    {
                        deny.Add(login);
                    }
                    break;
                default:
                    throw new InvalidOperationException($"{where}: \"{(property.Name.Length > 30 ? property.Name[..30] + "..." : property.Name)}\" is not allowed here (a project can only tighten: who, deny).");
            }
        }
        return new ProjectCommandRule(who, deny);
    }

    private static CommandLevel Level(string? text, string where) => text switch
    {
        "write" => CommandLevel.Write,
        "admin" => CommandLevel.Admin,
        _ => throw new InvalidOperationException($"{where} must be write or admin (a project can only tighten, and cannot name people to let in)."),
    };
}

/// <summary>The command rule of a host: a default for its repositories, and the exceptions the operator made for single ones.</summary>
public sealed partial record HostCommandRules(CommandRule Default, IReadOnlyDictionary<string, CommandRule> Repositories)
{
    /// <summary>No rule at all: everyone, on every repository.</summary>
    public static readonly HostCommandRules Open = new(CommandRule.Open, new Dictionary<string, CommandRule>());

    /// <summary>The rule of a repository: its own when the operator named it (case does not matter), the host's otherwise. The owner is the host's own word, not the factory's qualified one.</summary>
    public CommandRule For(string owner, string repo) =>
        Repositories.TryGetValue($"{owner}/{repo}", out var rule) ? rule : Default;

    /// <summary>Whether any rule holds anyone back, on any repository.</summary>
    public bool IsOpen => Default.IsOpen && Repositories.Values.All(rule => rule.IsOpen);

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,100}/[A-Za-z0-9._-]{1,100}\z")]
    private static partial Regex RepositoryPattern();

    /// <summary>Reads a host's <c>commands</c> and <c>repositories</c> settings; neither set means open.</summary>
    public static HostCommandRules Read(JsonElement host, string where)
    {
        if (!host.TryGetProperty("commands", out _) && !host.TryGetProperty("repositories", out _))
        {
            return Open;
        }
        var rule = host.TryGetProperty("commands", out var commands) ? CommandRule.Read(commands, $"{where}: commands") : CommandRule.Open;
        var repositories = new Dictionary<string, CommandRule>(StringComparer.OrdinalIgnoreCase);
        if (host.TryGetProperty("repositories", out var list))
        {
            if (list.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"{where}: repositories must be an object of owner/name to settings.");
            }
            foreach (var repository in list.EnumerateObject())
            {
                if (!RepositoryPattern().IsMatch(repository.Name))
                {
                    throw new InvalidOperationException($"{where}: \"{(repository.Name.Length > 40 ? repository.Name[..40] + "..." : repository.Name)}\" in repositories must look like owner/name.");
                }
                if (repository.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"{where}: repositories.{repository.Name} must be an object with \"commands\".");
                }
                CommandRule? own = null;
                foreach (var property in repository.Value.EnumerateObject())
                {
                    if (property.Name == "start")
                    {
                        continue; // read by HostStartRules
                    }
                    if (property.Name != "commands")
                    {
                        throw new InvalidOperationException($"{where}: repositories.{repository.Name} has an unknown setting (only commands and start).");
                    }
                    own = CommandRule.Read(property.Value, $"{where}: repositories.{repository.Name}.commands");
                }
                repositories[repository.Name] = own ?? rule;
            }
        }
        return new HostCommandRules(rule, repositories);
    }
}
