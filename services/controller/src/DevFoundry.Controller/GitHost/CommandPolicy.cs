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
/// </summary>
public static class CommandPolicy
{
    /// <summary>Whether the host has to be asked at all: only a level that depends on access does, and a person the rule names needs no asking.</summary>
    public static bool NeedsPermission(CommandRule rule, string login) =>
        rule.Who is CommandLevel.Write or CommandLevel.Admin
        && login.Length > 0
        && !rule.Denied.Contains(login)
        && !rule.Allowed.Contains(login);

    public static CommandVerdict Decide(CommandRule rule, string login, HostPermission? permission)
    {
        if (rule.IsOpen)
        {
            return CommandVerdict.Yes;
        }
        if (login.Length == 0)
        {
            return Refuse(rule, CommandRefusal.CouldNotCheck);
        }
        if (rule.Denied.Contains(login))
        {
            return Refuse(rule, CommandRefusal.Denied);
        }
        if (rule.Allowed.Contains(login))
        {
            return CommandVerdict.Yes;
        }
        switch (rule.Who)
        {
            case CommandLevel.Anyone:
                return CommandVerdict.Yes;
            case CommandLevel.None:
                return Refuse(rule, CommandRefusal.NotListed);
        }
        var needed = rule.Who == CommandLevel.Admin ? HostPermission.Admin : HostPermission.Write;
        if (permission is null or HostPermission.Unknown)
        {
            return Refuse(rule, CommandRefusal.CouldNotCheck);
        }
        return permission >= needed ? CommandVerdict.Yes : Refuse(rule, CommandRefusal.TooLittleAccess);
    }

    private static CommandVerdict Refuse(CommandRule rule, CommandRefusal why) =>
        new(false, why, rule.Who switch { CommandLevel.Write => "write", CommandLevel.Admin => "admin", _ => "" }, rule.Silent);
}
