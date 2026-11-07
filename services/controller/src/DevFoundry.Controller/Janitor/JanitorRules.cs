using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

public enum WipAction
{
    /// <summary>Too young, or something still uses it.</summary>
    Keep,

    /// <summary>Old enough to mention in the log, but not deleted.</summary>
    Report,

    /// <summary>Old, its ticket is closed, nothing uses it, and the operator allowed deletion.</summary>
    Delete,
}

/// <summary>The decisions of the janitor as plain functions, so the cautious rules can be tested without Forgejo or Docker.</summary>
public static partial class JanitorRules
{
    /// <summary>Only folders the factory itself makes (named after a workflow id) are ever removed; anything else in the root is not ours.</summary>
    private static readonly string[] OurPrefixes = [$"{WorkIds.Feature}-", $"{WorkIds.Bug}-", $"{WorkIds.Incident}-", $"{WorkIds.Revision}-"];

    [GeneratedRegex(@"\Afactory/issue-(?<number>\d+)-wip(?:-\d+)?\z")]
    private static partial Regex WipBranch();

    public static bool IsOurWorkspace(string name) => OurPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>A checkout is stale only if its run is gone <b>and</b> nothing touched it for the configured days.</summary>
    public static bool CheckoutIsStale(TimeSpan untouchedFor, bool runStillRunning, int days) =>
        !runStillRunning && untouchedFor >= TimeSpan.FromDays(days);

    /// <summary>The ticket number of a work-in-progress branch, or null for any other branch.</summary>
    public static int? WipIssueNumber(string branch) =>
        WipBranch().Match(branch) is { Success: true } match && int.TryParse(match.Groups["number"].Value, out var number) ? number : null;

    public static WipAction DecideWip(TimeSpan age, bool ticketClosed, bool hasOpenPullRequest, int days, bool deleteAllowed)
    {
        if (age < TimeSpan.FromDays(days) || hasOpenPullRequest)
        {
            return WipAction.Keep;
        }
        return deleteAllowed && ticketClosed ? WipAction.Delete : WipAction.Report;
    }
}
