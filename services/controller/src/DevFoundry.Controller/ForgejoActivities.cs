using Temporalio.Activities;

namespace DevFoundry.Controller;

public sealed record CommentRequest(string Owner, string Repo, int IssueNumber, string Body);

public sealed class ForgejoActivities(ForgejoClient forgejo)
{
    [Activity("forgejo_post_issue_comment")]
    public Task PostIssueCommentAsync(CommentRequest request) =>
        forgejo.PostIssueCommentAsync(
            request.Owner, request.Repo, request.IssueNumber, request.Body,
            ActivityExecutionContext.Current.CancellationToken);
}
