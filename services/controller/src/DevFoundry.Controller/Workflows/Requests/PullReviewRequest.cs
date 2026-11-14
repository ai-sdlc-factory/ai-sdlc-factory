namespace DevFoundry.Controller;

/// <summary>
/// A person asked for a review of a pull request: <c>/review</c> in a comment on it. It can be anyone's pull request, not only the factory's, because the review
/// writes nothing: the reviewers read the diff and post their findings. There is one review at a time per pull request.
/// </summary>
public sealed record PullReviewRequest(string Owner, string Repo, int Number, string Author)
{
    public string WorkflowId => WorkIds.For(Owner, Repo, Number, WorkIds.Review);

    /// <summary>The same run as a ticket-shaped request, so the shared workflow code can comment on the pull request and name its files.</summary>
    public WorkRequest ToWorkRequest(string title, string body = "") => new(Owner, Repo, Number, title, body, Author, Kind: WorkIds.Review);
}

/// <summary>What a review needs to know before it starts: the pull request, and what it changes (null when Forgejo could not say).</summary>
public sealed record ReviewContext(PullRequestDetails Pull, PullChanges? Changes);

/// <summary>When a review goes ahead: on a pull request that is open.</summary>
public static class PullReviewRules
{
    public static RevisionRefusal? Refuse(ReviewContext context)
    {
        if (context.Pull.Merged || !string.Equals(context.Pull.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            return new RevisionRefusal("ignored", TicketComments.PullReviewClosed);
        }
        return null;
    }
}
