using Temporalio.Activities;

namespace DevFoundry.Controller;

public sealed record CommentRequest(string Owner, string Repo, int IssueNumber, string Body);

public sealed record CreateIssueRequest(string Owner, string Repo, string Title, string Body, IReadOnlyList<string> Labels);

public sealed record PullRequestRequest(string Owner, string Repo, string Head, string Base, string Title, string Body);

public sealed class ForgejoActivities(ForgejoClient forgejo, WorkerSettings settings)
{
    [Activity("forgejo_post_issue_comment")]
    public Task PostIssueCommentAsync(CommentRequest request) =>
        forgejo.PostIssueCommentAsync(
            request.Owner, request.Repo, request.IssueNumber, request.Body,
            ActivityExecutionContext.Current.CancellationToken);

    [Activity("forgejo_post_notice_once")]
    public Task<bool> PostNoticeOnceAsync(CommentRequest request) =>
        forgejo.PostNoticeOnceAsync(
            request.Owner, request.Repo, request.IssueNumber, request.Body,
            ActivityExecutionContext.Current.CancellationToken);

    [Activity("forgejo_create_issue")]
    public Task<IssueInfo> CreateIssueAsync(CreateIssueRequest request) =>
        forgejo.CreateIssueAsync(
            request.Owner, request.Repo, request.Title, request.Body, request.Labels,
            ActivityExecutionContext.Current.CancellationToken);

    /// <summary>Reads what is needed to decide whether to revise a pull request, and what the reviewer asked for.</summary>
    [Activity("forgejo_read_revision_context")]
    public async Task<RevisionContext> ReadRevisionContextAsync(RevisionRequest request)
    {
        var token = ActivityExecutionContext.Current.CancellationToken;
        var bot = settings.ForgejoBotLogin;
        var pull = await forgejo.GetPullRequestAsync(request.Owner, request.Repo, request.PullNumber, token);
        var review = await forgejo.GetLatestReviewAsync(request.Owner, request.Repo, request.PullNumber, bot, token);
        var prior = await forgejo.CountRevisionsAsync(request.Owner, request.Repo, request.PullNumber, token);
        return new RevisionContext(pull, bot, review, prior);
    }

    [Activity("forgejo_create_pull_request")]
    public Task<PullRequestInfo> CreatePullRequestAsync(PullRequestRequest request) =>
        forgejo.CreatePullRequestAsync(
            request.Owner, request.Repo, request.Head, request.Base, request.Title, request.Body,
            ActivityExecutionContext.Current.CancellationToken);
}
