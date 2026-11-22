namespace AiSdlcFactory.Controller;

/// <summary>
/// A person asked for a deeper test of a finished change: <c>/qa</c> (or <c>/qa api</c>, <c>/qa ui</c>) in a comment on the ticket or on the factory's
/// pull request. <see cref="Number"/> is the number of the ticket or pull request the comment is on; the report is posted there. There is one QA run
/// at a time per number.
/// </summary>
/// <param name="OnPullRequest">The comment is on the pull request itself; otherwise on the ticket, whose pull request is looked up.</param>
/// <param name="Suite">Run only this suite (<c>api</c> or <c>ui</c>); null runs every suite the project lists.</param>
/// <param name="Mode"><see cref="Focused"/> tests the change the pull request makes; <see cref="Full"/> also tests the application as a whole.</param>
/// <param name="Note">What the person asked to be taken into account (the rest of their <c>/qa</c> comment), or null. Text from a person: data, not instructions.</param>
public sealed record QaRequest(
    string Owner, string Repo, int Number, bool OnPullRequest, string? Suite, string Author, string Mode = QaRequest.Focused, string? Note = null)
{
    public const string Focused = "focused";
    public const string Full = "full";

    public string WorkflowId => WorkIds.For(Owner, Repo, Number, WorkIds.Qa);

    /// <summary>The same run as a ticket-shaped request, so the shared workflow code can comment where the person asked and name its files.</summary>
    public WorkRequest ToWorkRequest(string title) => new(Owner, Repo, Number, title, "", Author, Kind: WorkIds.Qa);
}
