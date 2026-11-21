namespace DevFoundry.Controller;

/// <summary>Why a person may not steer the factory, as the answer to a command says it. Never names anyone.</summary>
public enum CommandRefusal
{
    None,

    /// <summary>The operator listed this person as refused.</summary>
    Denied,

    /// <summary>The rule lets in only the people it names, and this one is not named.</summary>
    NotListed,

    /// <summary>The host says the person has less access than the rule asks for.</summary>
    TooLittleAccess,

    /// <summary>The factory could not find out what the person may do (the host could not say, or answered with an error), so it did not take the risk.</summary>
    CouldNotCheck,
}

/// <summary>What the check of one person's right to steer the factory decided.</summary>
public sealed record CommandVerdict(bool Allowed, CommandRefusal Refusal, string Needs, bool Silent)
{
    public static readonly CommandVerdict Yes = new(true, CommandRefusal.None, "", false);
}

/// <summary>
/// The rule of who may steer the factory, applied. Pure: what the host says about the person is given in, so the same rule and the same answer always decide the same way. A refused or
/// unknown person is refused, never let in: when in doubt the answer is no.
/// <para>
/// A person has to pass, in this order: the host's (or repository's) rule, then the rule of each command the comment carries (on top of it, so it can only be stricter), then, where the operator
/// allowed it, what the repository's own project file tightens. People the operator named in an <c>allow</c> list are not touched by the repository's say.
/// </para>
/// </summary>
public static class CommandPolicy
{
    /// <summary>Whether the host has to be asked at all: only a level that depends on access does, and a person a rule names needs no asking.</summary>
    public static bool NeedsPermission(CommandRule rule, string login, IReadOnlyList<string>? commands = null, ProjectCommandRule? project = null)
    {
        if (login.Length == 0)
        {
            return false;
        }
        if (Steps(rule, commands).Any(step => StepNeedsPermission(step, login)))
        {
            return true;
        }
        return project is { Tightens: true, Who: CommandLevel.Write or CommandLevel.Admin }
            && !project.Deny.Contains(login)
            && !NamedByOperator(rule, login, commands);
    }

    public static CommandVerdict Decide(CommandRule rule, string login, HostPermission? permission, IReadOnlyList<string>? commands = null, ProjectCommandRule? project = null)
    {
        foreach (var step in Steps(rule, commands))
        {
            var verdict = DecideStep(step, login, permission);
            if (!verdict.Allowed)
            {
                return verdict;
            }
        }
        if (project is { Tightens: true } && !NamedByOperator(rule, login, commands))
        {
            if (login.Length == 0)
            {
                return Refuse(rule, CommandRefusal.CouldNotCheck, CommandLevel.Anyone);
            }
            if (project.Deny.Contains(login))
            {
                return Refuse(rule, CommandRefusal.Denied, CommandLevel.Anyone);
            }
            if (project.Who is CommandLevel.Write or CommandLevel.Admin)
            {
                return Compare(rule, project.Who, permission);
            }
        }
        return CommandVerdict.Yes;
    }

    /// <summary>The rule itself, then the rule of each command the comment carries.</summary>
    private static IEnumerable<CommandRule> Steps(CommandRule rule, IReadOnlyList<string>? commands)
    {
        yield return rule;
        foreach (var name in commands ?? [])
        {
            if (rule.EachCommand.TryGetValue(name, out var own))
            {
                yield return own;
            }
        }
    }

    private static bool NamedByOperator(CommandRule rule, string login, IReadOnlyList<string>? commands) =>
        rule.Allowed.Contains(login) || (commands ?? []).Any(name => rule.EachCommand.TryGetValue(name, out var own) && own.Allowed.Contains(login));

    private static bool StepNeedsPermission(CommandRule step, string login) =>
        step.Who is CommandLevel.Write or CommandLevel.Admin && !step.Denied.Contains(login) && !step.Allowed.Contains(login);

    private static CommandVerdict DecideStep(CommandRule rule, string login, HostPermission? permission)
    {
        if (!rule.HoldsBack)
        {
            return CommandVerdict.Yes;
        }
        if (login.Length == 0)
        {
            return Refuse(rule, CommandRefusal.CouldNotCheck, rule.Who);
        }
        if (rule.Denied.Contains(login))
        {
            return Refuse(rule, CommandRefusal.Denied, rule.Who);
        }
        if (rule.Allowed.Contains(login))
        {
            return CommandVerdict.Yes;
        }
        return rule.Who switch
        {
            CommandLevel.Anyone => CommandVerdict.Yes,
            CommandLevel.None => Refuse(rule, CommandRefusal.NotListed, rule.Who),
            _ => Compare(rule, rule.Who, permission),
        };
    }

    private static CommandVerdict Compare(CommandRule rule, CommandLevel level, HostPermission? permission)
    {
        var needed = level == CommandLevel.Admin ? HostPermission.Admin : HostPermission.Write;
        if (permission is null or HostPermission.Unknown)
        {
            return Refuse(rule, CommandRefusal.CouldNotCheck, level);
        }
        return permission >= needed ? CommandVerdict.Yes : Refuse(rule, CommandRefusal.TooLittleAccess, level);
    }

    private static CommandVerdict Refuse(CommandRule rule, CommandRefusal why, CommandLevel level) =>
        new(false, why, level switch { CommandLevel.Write => "write", CommandLevel.Admin => "admin", _ => "" }, rule.Silent);
}
