using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSdlcFactory.Contracts;

/// <summary>The kinds of run a label (or <c>/factory</c>, or <c>/retry</c>) can start, by the names the workflows use.</summary>
public static class StartKinds
{
    public static readonly IReadOnlyList<string> All = ["feature", "bug", "plan", "incident", "docs"];
}

/// <summary>
/// Who may START a run on a repository, by the operator's rule (<c>start</c> in <c>FACTORY_HOSTS</c>). The person it judges is the one who set the run off: who added the label, who opened a ticket
/// that already carried one, who reopened it, or who commented <c>/factory</c> or <c>/retry</c>. It is separate from <see cref="CommandRule"/> (who may steer a run that exists) and off by default.
/// A person is let in when they are named in <see cref="Allow"/>, belong to one of the <see cref="Teams"/>, or have the access <see cref="Who"/> asks for, and are not in <see cref="Deny"/>
/// (deny first). <see cref="Kinds"/> limits the rule to some kinds of run; the others start as they always did.
/// </summary>
public sealed partial record StartRule(
    CommandLevel Who = CommandLevel.Anyone,
    IReadOnlySet<string>? Allow = null,
    IReadOnlySet<string>? Deny = null,
    IReadOnlyList<string>? Teams = null,
    IReadOnlySet<string>? Kinds = null,
    bool Silent = false,
    string? Message = null)
{
    /// <summary>Everyone, as before there was a rule.</summary>
    public static readonly StartRule Open = new();

    public const int MaxTeams = 20;
    public const int MaxMessage = 500;

    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>();

    public IReadOnlySet<string> Allowed => Allow ?? NoNames;
    public IReadOnlySet<string> Denied => Deny ?? NoNames;
    public IReadOnlyList<string> TeamList => Teams ?? [];

    /// <summary>True when nobody is held back: the lowest level and no one denied (a list of names or teams only matters where the level lets in less).</summary>
    public bool IsOpen => Who == CommandLevel.Anyone && Denied.Count == 0;

    /// <summary>Whether this rule judges a run of this kind: it holds someone back, and is not limited to other kinds.</summary>
    public bool Covers(string kind) => !IsOpen && (Kinds is null || Kinds.Contains(kind));

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,100}/[A-Za-z0-9._-]{1,100}\z")]
    private static partial Regex TeamPattern();

    /// <summary>One line for the log, with no one's name: what the rule asks of a person.</summary>
    public string Describe()
    {
        if (IsOpen)
        {
            return "anyone may start a run";
        }
        var parts = new List<string>();
        if (Who == CommandLevel.Write)
        {
            parts.Add("people with write access");
        }
        else if (Who == CommandLevel.Admin)
        {
            parts.Add("people with admin access");
        }
        if (TeamList.Count > 0)
        {
            parts.Add($"members of {TeamList.Count} team{(TeamList.Count == 1 ? "" : "s")}");
        }
        if (Allowed.Count > 0)
        {
            parts.Add($"{Allowed.Count} named {(Allowed.Count == 1 ? "person" : "people")}");
        }
        var who = parts.Count == 0 ? (Who == CommandLevel.Anyone ? "anyone" : "nobody") : string.Join(", ", parts);
        var kinds = Kinds is null ? "" : $" for {string.Join("/", StartKinds.All.Where(Kinds.Contains))}";
        return $"a run{kinds} starts only when set off by {who}{(Denied.Count > 0 ? $", except {Denied.Count} refused" : "")}";
    }

    /// <summary>
    /// Reads a <c>start</c> setting: a level as text (<c>anyone</c>, <c>write</c>, <c>admin</c>, <c>none</c>), or an object with <c>who</c>, <c>allow</c>, <c>deny</c>, <c>teams</c> (<c>org/team</c>), <c>kinds</c>,
    /// <c>refusal</c> (<c>comment</c> or <c>silent</c>) and <c>message</c>. Without <c>who</c>, a list of people or teams means only them, and no list means anyone but the people denied.
    /// </summary>
    public static StartRule Read(JsonElement value, string where)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return new StartRule(Level(value.GetString(), where));
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{where} must be a level (anyone, write, admin, none) or an object.");
        }
        CommandLevel? who = null;
        HashSet<string>? allow = null, deny = null, kinds = null;
        List<string>? teams = null;
        var silent = false;
        string? message = null;
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "who":
                    who = Level(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null, $"{where}: who");
                    break;
                case "allow":
                    allow = CommandRule.ReadLogins(property.Value, $"{where}: allow");
                    break;
                case "deny":
                    deny = CommandRule.ReadLogins(property.Value, $"{where}: deny");
                    break;
                case "teams":
                    teams = Teams_(property.Value, $"{where}: teams");
                    break;
                case "kinds":
                    kinds = Kinds_(property.Value, $"{where}: kinds");
                    break;
                case "refusal":
                    silent = (property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null) switch
                    {
                        "silent" => true,
                        "comment" => false,
                        _ => throw new InvalidOperationException($"{where}: refusal must be \"comment\" or \"silent\"."),
                    };
                    break;
                case "message":
                    message = Message_(property.Value, $"{where}: message");
                    break;
                default:
                    throw new InvalidOperationException($"{where}: unknown setting \"{Short(property.Name)}\" (who, allow, deny, teams, kinds, refusal, message).");
            }
        }
        // A list of people or teams with no level means only them; with neither, nobody is held back by the level.
        var level = who ?? (allow is { Count: > 0 } || teams is { Count: > 0 } ? CommandLevel.None : CommandLevel.Anyone);
        return new StartRule(level, allow, deny, teams, kinds, silent, message);
    }

    private static CommandLevel Level(string? text, string where) => text switch
    {
        "anyone" => CommandLevel.Anyone,
        "write" => CommandLevel.Write,
        "admin" => CommandLevel.Admin,
        "none" => CommandLevel.None,
        _ => throw new InvalidOperationException($"{where} must be one of anyone, write, admin, none."),
    };

    private static List<string> Teams_(JsonElement list, string where)
    {
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaxTeams)
        {
            throw new InvalidOperationException($"{where} must be a list of 1 to {MaxTeams} teams, each written organisation/team, for example [\"acme/maintainers\"].");
        }
        var teams = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            var team = item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : "";
            if (!TeamPattern().IsMatch(team))
            {
                throw new InvalidOperationException($"{where}: \"{Short(team)}\" is not a team: write it organisation/team (letters, digits, dots, dashes and underscores).");
            }
            if (!teams.Contains(team, StringComparer.OrdinalIgnoreCase))
            {
                teams.Add(team);
            }
        }
        return teams;
    }

    private static HashSet<string> Kinds_(JsonElement list, string where)
    {
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"{where} must be a list of the kinds of run it covers ({string.Join(", ", StartKinds.All)}).");
        }
        var kinds = new HashSet<string>();
        foreach (var item in list.EnumerateArray())
        {
            var kind = item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
            if (!StartKinds.All.Contains(kind))
            {
                throw new InvalidOperationException($"{where}: \"{Short(kind)}\" is not a kind of run ({string.Join(", ", StartKinds.All)}).");
            }
            kinds.Add(kind);
        }
        return kinds;
    }

    private static string Message_(JsonElement value, string where)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
        if (text.Length is 0 or > MaxMessage || text.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{where} must be one line of plain text of 1 to {MaxMessage} characters.");
        }
        return text;
    }

    private static string Short(string value) => value.Length > 40 ? value[..40] + "..." : value;
}

/// <summary>The start rule of a host: a default for its repositories, and the exceptions the operator made for single ones.</summary>
public sealed record HostStartRules(StartRule Default, IReadOnlyDictionary<string, StartRule> Repositories)
{
    /// <summary>No rule at all: anyone may start a run, on every repository.</summary>
    public static readonly HostStartRules Open = new(StartRule.Open, new Dictionary<string, StartRule>());

    /// <summary>The rule of a repository: its own when the operator named it (case does not matter), the host's otherwise. The owner is the host's own word, not the factory's qualified one.</summary>
    public StartRule For(string owner, string repo) =>
        Repositories.TryGetValue($"{owner}/{repo}", out var rule) ? rule : Default;

    /// <summary>Whether any rule holds anyone back, on any repository.</summary>
    public bool IsOpen => Default.IsOpen && Repositories.Values.All(rule => rule.IsOpen);

    /// <summary>
    /// Reads a host's <c>start</c> setting and the <c>start</c> of each entry of its <c>repositories</c>; none set means open. The shape of <c>repositories</c> itself was checked when the
    /// <c>commands</c> were read, so an entry here is known to be an object.
    /// </summary>
    public static HostStartRules Read(JsonElement host, string where)
    {
        var rule = host.TryGetProperty("start", out var start) ? StartRule.Read(start, $"{where}: start") : StartRule.Open;
        var repositories = new Dictionary<string, StartRule>(StringComparer.OrdinalIgnoreCase);
        if (host.TryGetProperty("repositories", out var list) && list.ValueKind == JsonValueKind.Object)
        {
            foreach (var repository in list.EnumerateObject())
            {
                if (repository.Value.ValueKind == JsonValueKind.Object && repository.Value.TryGetProperty("start", out var own))
                {
                    repositories[repository.Name] = StartRule.Read(own, $"{where}: repositories.{repository.Name}.start");
                }
            }
        }
        return rule.IsOpen && repositories.Count == 0 ? Open : new HostStartRules(rule, repositories);
    }
}
