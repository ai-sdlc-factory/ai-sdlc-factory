using System.Text.RegularExpressions;

namespace AiSdlcFactory.Controller;

/// <summary>
/// A person asked the factory to act on its own pull request: either a review that requested changes, or a comment with the
/// <c>/fix</c> command. The id and the workspace name use the pull request's number, so there is one revision at a time per pull request.
/// </summary>
public sealed record RevisionRequest(string Owner, string Repo, int PullNumber, string Trigger, string Instruction, string Author)
{
    public const string ByReview = "review";
    public const string ByCommand = "command";

    public string WorkflowId => WorkIds.For(Owner, Repo, PullNumber, WorkIds.Revision);

    /// <summary>The same pull request as a ticket-shaped request, so the shared workflow code can comment on it and name its files.</summary>
    public WorkRequest ToWorkRequest(string title) =>
        new(Owner, Repo, PullNumber, title, Instruction, Author, Kind: WorkIds.Revision);
}

/// <summary>Why a revision does not go ahead. <see cref="Message"/> is null when it should end without a word.</summary>
public sealed record RevisionRefusal(string Status, string? Message);

public static partial class RevisionRules
{
    /// <summary>A pre-check before the project's own limit is known (it needs the checkout): nothing can have more revisions than this.</summary>
    public const int MaxRevisionsPerPullRequest = LimitRanges.MaxRevisions;
    public const int MaxComments = 30;

    /// <summary>The ticket a factory branch was made for: <c>factory/issue-12</c> and <c>factory/issue-12-2</c> are both ticket 12.</summary>
    public static int? TicketOfBranch(string branch) =>
        Regex.Match(branch, @"\Afactory/issue-(\d+)(?:-\d+)?\z") is { Success: true } match && int.TryParse(match.Groups[1].Value, out var number) ? number : null;

    /// <summary>The branches the factory creates for tickets: <c>factory/issue-12</c>, or <c>factory/issue-12-2</c> when the name was taken.</summary>
    [GeneratedRegex(@"\Afactory/issue-\d+(?:-\d+)?\z")]
    public static partial Regex FactoryBranch();

    /// <summary>Whether a pull request is one the factory opened from its own branch in the same repository and that is still open: the only kind a hint about commands is for.</summary>
    public static bool IsOpenFactoryPull(PullRequestDetails pull, string botLogin) =>
        FactoryBranch().IsMatch(pull.HeadRef)
        && string.Equals(pull.Author, botLogin, StringComparison.OrdinalIgnoreCase)
        && string.Equals(pull.HeadRepository, pull.BaseRepository, StringComparison.OrdinalIgnoreCase)
        && !pull.Merged
        && string.Equals(pull.State, "open", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a pull request may be revised. Only a pull request the factory itself opened, from a branch in the same repository,
    /// that is still open and has been revised fewer than three times. Anything else is left alone.
    /// </summary>
    public static RevisionRefusal? Refuse(RevisionRequest request, RevisionContext context)
    {
        var pull = context.Pull;
        if (!FactoryBranch().IsMatch(pull.HeadRef) || !string.Equals(pull.Author, context.BotLogin, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pull.HeadRepository, pull.BaseRepository, StringComparison.OrdinalIgnoreCase))
        {
            // A review on somebody else's pull request is ordinary activity and gets no answer; a /fix typed there asks the factory for something, so it is told why not.
            return new RevisionRefusal("ignored", request.Trigger == RevisionRequest.ByCommand ? TicketComments.RevisionNotFactorys : null);
        }
        if (pull.Merged || !string.Equals(pull.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            return new RevisionRefusal("ignored", TicketComments.RevisionClosed);
        }
        if (context.PriorRevisions >= MaxRevisionsPerPullRequest)
        {
            return new RevisionRefusal("revision_limit", TicketComments.RevisionLimit(MaxRevisionsPerPullRequest));
        }
        if (string.IsNullOrWhiteSpace(request.Instruction) && context.Reviews.Count == 0)
        {
            return new RevisionRefusal("nothing_to_do", TicketComments.RevisionNothingToDo);
        }
        return null;
    }
}
