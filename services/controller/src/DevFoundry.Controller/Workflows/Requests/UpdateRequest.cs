namespace DevFoundry.Controller;

/// <summary>
/// A person asked the factory to bring its pull request up to date with the base branch: <c>/update</c> in a comment on the ticket or on the pull
/// request. <see cref="Number"/> is the number of the ticket or pull request the comment is on; the progress and the result are posted there. There
/// is one update at a time per number.
/// </summary>
/// <param name="OnPullRequest">The comment is on the pull request itself; otherwise on the ticket, whose pull request is looked up.</param>
public sealed record UpdateRequest(string Owner, string Repo, int Number, bool OnPullRequest, string Author)
{
    public string WorkflowId => WorkIds.For(Owner, Repo, Number, WorkIds.Update);

    /// <summary>The same run as a ticket-shaped request, so the shared workflow code can comment where the person asked and name its files.</summary>
    public WorkRequest ToWorkRequest(string title) => new(Owner, Repo, Number, title, "", Author, Kind: WorkIds.Update);
}

/// <summary>When an update goes ahead: only on an open pull request that the factory itself opened, from a branch in the same repository.</summary>
public static class UpdateRules
{
    public static RevisionRefusal? Refuse(UpdateRequest request, UpdateContext context)
    {
        if (context.Pull is not { } pull)
        {
            return new RevisionRefusal(Statuses.UpdateNoPullRequest, TicketComments.UpdateNoPullRequest);
        }
        if (!RevisionRules.FactoryBranch().IsMatch(pull.HeadRef) || !string.Equals(pull.Author, context.BotLogin, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pull.HeadRepository, pull.BaseRepository, StringComparison.OrdinalIgnoreCase))
        {
            // A comment on somebody else's pull request is none of the factory's business.
            return new RevisionRefusal(request.OnPullRequest ? "ignored" : Statuses.UpdateNoPullRequest, request.OnPullRequest ? null : TicketComments.UpdateNoPullRequest);
        }
        if (pull.Merged || !string.Equals(pull.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            return new RevisionRefusal("ignored", TicketComments.UpdateClosed);
        }
        return null;
    }
}
