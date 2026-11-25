namespace AiSdlcFactory.Controller;

/// <summary>What the check of one person's right to START a run decided. <see cref="Needs"/> says what the rule asks for, never who may; <see cref="CouldNotCheck"/> means the host could not say.</summary>
public sealed record StartVerdict(bool Allowed, string Kind, string Needs, bool Silent, string? Message, bool CouldNotCheck = false)
{
    public static readonly StartVerdict Yes = new(true, "", "", false, null);
}

/// <summary>The identity of one start to check: the host, the repository, the person who set it off, and the kind of run it would be.</summary>
public sealed record StartCheck(string Host, string Owner, string Repo, string Login, string Kind);

/// <summary>
/// The rule of who may start a run, applied. Pure: what the host says about the person (their access, and whether they are in the teams the rule names) is given in, so the same rule and the
/// same answers always decide the same way. A person it cannot be sure of is refused, never let in. Order: not covered, then denied, then named, then a team, then the level.
/// </summary>
public static class StartPolicy
{
    /// <summary>Whether the host has to be asked about the person's access at all: only a level that depends on access does, and only for a person the rule does not already settle.</summary>
    public static bool NeedsPermission(StartRule rule, string login) =>
        login.Length > 0 && rule.Who is CommandLevel.Write or CommandLevel.Admin && !rule.Denied.Contains(login) && !rule.Allowed.Contains(login);

    /// <summary>The teams still worth asking the host about: none for a person the rule already settles, or whose access already lets them in.</summary>
    public static IReadOnlyList<string> TeamsToAsk(StartRule rule, string login, HostPermission? permission) =>
        login.Length == 0 || rule.Denied.Contains(login) || rule.Allowed.Contains(login) || LevelMet(rule, permission) ? [] : rule.TeamList;

    public static StartVerdict Decide(StartRule rule, string kind, string login, HostPermission? permission, IReadOnlyDictionary<string, bool> teamMembership)
    {
        if (!rule.Covers(kind))
        {
            return StartVerdict.Yes;
        }
        if (login.Length == 0)
        {
            return Refuse(rule, kind, couldNotCheck: true);
        }
        if (rule.Denied.Contains(login))
        {
            return Refuse(rule, kind, couldNotCheck: false);
        }
        if (rule.Allowed.Contains(login) || rule.TeamList.Any(team => teamMembership.TryGetValue(team, out var member) && member) || LevelMet(rule, permission))
        {
            return StartVerdict.Yes;
        }
        // Refused. It matters whether that is a clear no or the host could not say, because the person is told which.
        var unknownAccess = rule.Who is CommandLevel.Write or CommandLevel.Admin && permission is null or HostPermission.Unknown;
        var unknownTeam = rule.TeamList.Any(team => !teamMembership.ContainsKey(team));
        return Refuse(rule, kind, couldNotCheck: unknownAccess || unknownTeam);
    }

    private static bool LevelMet(StartRule rule, HostPermission? permission) =>
        rule.Who switch
        {
            CommandLevel.Anyone => true,
            CommandLevel.Write => permission >= HostPermission.Write,
            CommandLevel.Admin => permission >= HostPermission.Admin,
            _ => false,
        };

    private static StartVerdict Refuse(StartRule rule, string kind, bool couldNotCheck) => new(false, kind, Needs(rule), rule.Silent, rule.Message, couldNotCheck);

    /// <summary>What the rule asks of a person, in words that name no one: the access, the teams (the operator's own words), and being on the repository's list.</summary>
    public static string Needs(StartRule rule)
    {
        var parts = new List<string>();
        if (rule.Who == CommandLevel.Write)
        {
            parts.Add("write access to the repository");
        }
        else if (rule.Who == CommandLevel.Admin)
        {
            parts.Add("admin access to the repository");
        }
        if (rule.TeamList.Count > 0)
        {
            parts.Add($"membership of {string.Join(" or ", rule.TeamList)}");
        }
        if (rule.Allowed.Count > 0)
        {
            parts.Add("being on the list of people the operator named");
        }
        return parts.Count == 0 ? "someone the operator allows" : string.Join(", or ", parts);
    }
}
