using System.Text.RegularExpressions;

namespace AiSdlcFactory.Controller;

/// <summary>What a pull request ended up as, when the report was made.</summary>
public static class PullStates
{
    public const string Merged = "merged";
    public const string ClosedUnmerged = "closed";
    public const string Open = "open";
}

/// <summary>A ticket that a run opened a pull request for, as the usage log says (a number and a kind, never any text).</summary>
public sealed record LoggedPullKind(string Repository, int Ticket, string Kind);

/// <summary>How many revisions the factory pushed to a pull request, as the usage log counts them.</summary>
public sealed record LoggedRevisions(string Repository, int Pull, int Count);

/// <summary>What the usage log says about the factory's pull requests: which repositories to ask the git host about, which ticket was what kind of run, and how many revisions each pull request got.</summary>
public sealed record PullScope(IReadOnlyList<string> Repositories, IReadOnlyList<LoggedPullKind> Tickets, IReadOnlyList<LoggedRevisions> Revisions);

/// <summary>What the weekly report asks of the git host: the pull requests the factory opened in [<see cref="From"/>, <see cref="To"/>) in these repositories, and the log's facts to join them to.</summary>
public sealed record PullOutcomeRequest(PullScope Scope, DateTimeOffset From, DateTimeOffset To);

/// <summary>One pull request of the factory and what became of it. Only numbers and states: nothing from its title or text is read.</summary>
public sealed record PullOutcome(string Repository, int Number, string Kind, string State, int Revisions, double? DaysToMerge);

/// <summary>A pull request as the git host lists it: only what the report needs.</summary>
public sealed record PullSummary(int Number, string State, bool Merged, string HeadRef, string Author, DateTimeOffset CreatedAt, DateTimeOffset? MergedAt);

public static partial class PullOutcomes
{
    /// <summary>The kind of a pull request whose ticket the usage log does not know (a run from before the log, or a branch made by hand with the factory's name).</summary>
    public const string UnknownKind = "other";

    [GeneratedRegex(@"\Afactory/issue-(?<ticket>\d+)(?:-\d+)?\z")]
    private static partial Regex FactoryBranch();

    /// <summary>
    /// The factory's own pull requests of the window: opened by the bot from a branch the factory names, created in [<paramref name="from"/>, <paramref name="to"/>). A pull request
    /// someone else opened, or one the bot opened by another route, is not the factory's work and is not counted. Pure: the same input always gives the same list.
    /// </summary>
    public static IReadOnlyList<PullOutcome> Build(string repository, IEnumerable<PullSummary> pulls, PullScope scope, string botLogin, DateTimeOffset from, DateTimeOffset to)
    {
        var kinds = scope.Tickets.Where(item => item.Repository == repository).GroupBy(item => item.Ticket).ToDictionary(group => group.Key, group => group.Last().Kind);
        var revisions = scope.Revisions.Where(item => item.Repository == repository).ToDictionary(item => item.Pull, item => item.Count);
        var outcomes = new List<PullOutcome>();
        foreach (var pull in pulls.OrderBy(pull => pull.Number))
        {
            if (pull.CreatedAt < from || pull.CreatedAt >= to || !string.Equals(pull.Author, botLogin, StringComparison.OrdinalIgnoreCase)
                || FactoryBranch().Match(pull.HeadRef) is not { Success: true } branch || !int.TryParse(branch.Groups["ticket"].Value, out var ticket))
            {
                continue;
            }
            var state = pull.Merged ? PullStates.Merged : pull.State == "open" ? PullStates.Open : PullStates.ClosedUnmerged;
            double? days = pull.Merged && pull.MergedAt is { } merged ? Math.Max(0, (merged - pull.CreatedAt).TotalDays) : null;
            outcomes.Add(new PullOutcome(repository, pull.Number, kinds.GetValueOrDefault(ticket, UnknownKind), state, revisions.GetValueOrDefault(pull.Number), days));
        }
        return outcomes;
    }
}
