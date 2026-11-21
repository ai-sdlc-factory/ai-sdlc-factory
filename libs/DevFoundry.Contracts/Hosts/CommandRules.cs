using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFoundry.Contracts;

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
    bool Silent = false)
{
    /// <summary>Everyone, as before there was a rule.</summary>
    public static readonly CommandRule Open = new();

    public IReadOnlySet<string> Allowed => Allow ?? EmptyNames;
    public IReadOnlySet<string> Denied => Deny ?? EmptyNames;

    private static readonly IReadOnlySet<string> EmptyNames = new HashSet<string>();

    /// <summary>True when nobody is held back: no one is denied and the level is the lowest.</summary>
    public bool IsOpen => Who == CommandLevel.Anyone && Denied.Count == 0;

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._@-]{0,99}\z")]
    private static partial Regex LoginPattern();

    /// <summary>One line for a log, with no one's name: what the rule asks of a person.</summary>
    public string Describe() => (Who, IsOpen) switch
    {
        (CommandLevel.Anyone, true) => "anyone who can comment may use commands",
        (CommandLevel.Anyone, false) => $"anyone who can comment may use commands, except {Denied.Count} named",
        (CommandLevel.Write, _) => $"commands need write access{Names()}",
        (CommandLevel.Admin, _) => $"commands need admin access{Names()}",
        _ => $"commands only for the {Allowed.Count} named" + (Denied.Count > 0 ? $" (and {Denied.Count} denied)" : ""),
    };

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
    public static CommandRule Read(JsonElement value, string where)
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
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
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
                    throw new InvalidOperationException($"{where}: unknown setting \"{Short(property.Name)}\" (who, allow, deny, refusal).");
            }
        }
        return new CommandRule(who, allow, deny, silent);
    }

    private static CommandLevel Level(string? text, string where) => text switch
    {
        "anyone" => CommandLevel.Anyone,
        "write" => CommandLevel.Write,
        "admin" => CommandLevel.Admin,
        "none" => CommandLevel.None,
        _ => throw new InvalidOperationException($"{where} must be one of anyone, write, admin, none."),
    };

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
                    if (property.Name != "commands")
                    {
                        throw new InvalidOperationException($"{where}: repositories.{repository.Name} has an unknown setting (only commands).");
                    }
                    own = CommandRule.Read(property.Value, $"{where}: repositories.{repository.Name}.commands");
                }
                repositories[repository.Name] = own ?? rule;
            }
        }
        return new HostCommandRules(rule, repositories);
    }
}
