namespace AiSdlcFactory.Controller;

/// <summary>
/// A person asked a question about the repository: <c>/ask</c> in a comment on a ticket or a pull request (a ticket needs no factory label; anyone's pull request will do).
/// The answer is read from the code and posted as a comment; nothing is changed. There is one question at a time per ticket or pull request.
/// </summary>
public sealed record AskRequest(string Owner, string Repo, int Number, bool OnPullRequest, string Author, string Question, string Title = "", string Body = "")
{
    public string WorkflowId => WorkIds.For(Owner, Repo, Number, WorkIds.Ask);

    /// <summary>The same run as a ticket-shaped request, so the shared workflow code can comment on the ticket or pull request.</summary>
    public WorkRequest ToWorkRequest() => new(Owner, Repo, Number, Title, Body, Author, Kind: WorkIds.Ask);
}
