using Temporalio.Activities;

namespace DevFoundry.Controller;

public sealed record CommentRequest(string Owner, string Repo, int IssueNumber, string Body);

public sealed record PullRequestRequest(string Owner, string Repo, string Head, string Base, string Title, string Body);

public sealed class ForgejoActivities(ForgejoClient forgejo)
{
    [Activity("forgejo_post_issue_comment")]
    public Task PostIssueCommentAsync(CommentRequest request) =>
        forgejo.PostIssueCommentAsync(
            request.Owner, request.Repo, request.IssueNumber, request.Body,
            ActivityExecutionContext.Current.CancellationToken);

    [Activity("forgejo_create_pull_request")]
    public Task<PullRequestInfo> CreatePullRequestAsync(PullRequestRequest request) =>
        forgejo.CreatePullRequestAsync(
            request.Owner, request.Repo, request.Head, request.Base, request.Title, request.Body,
            ActivityExecutionContext.Current.CancellationToken);
}
