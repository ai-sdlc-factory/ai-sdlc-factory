using Temporalio.Activities;

namespace DevFoundry.Controller;

public sealed record CommentRequest(string Owner, string Repo, int IssueNumber, string Body);

public sealed record CreateIssueRequest(string Owner, string Repo, string Title, string Body, IReadOnlyList<string> Labels);

public sealed record ReviewRequestInput(string Owner, string Repo, int PullNumber, IReadOnlyList<string> Reviewers);


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
        var history = await forgejo.GetRevisionHistoryAsync(request.Owner, request.Repo, request.PullNumber, token);
        var reviews = await forgejo.GetUnaddressedReviewsAsync(request.Owner, request.Repo, request.PullNumber, bot, history.LastAt, token);
        IReadOnlyList<string>? qaFindings = null;
        if (QaReportFormat.IsQaFix(request.Instruction))
        {
            qaFindings = await LatestQaFindingsAsync(request, pull, token);
        }
        return new RevisionContext(pull, bot, reviews, history.Count, qaFindings);
    }

    /// <summary>The findings of the newest QA report, looked for on the ticket the pull request belongs to and on the pull request itself.</summary>
    private async Task<IReadOnlyList<string>> LatestQaFindingsAsync(RevisionRequest request, PullRequestDetails pull, CancellationToken token)
    {
        var numbers = new List<int>();
        if (RevisionRules.TicketOfBranch(pull.HeadRef) is { } ticket)
        {
            numbers.Add(ticket);
        }
        numbers.Add(request.PullNumber);
        foreach (var number in numbers)
        {
            var bodies = await forgejo.RecentCommentBodiesAsync(request.Owner, request.Repo, number, 50, token);
            if (bodies.LastOrDefault(body => body.Contains(QaReportFormat.Marker, StringComparison.Ordinal)) is { } report)
            {
                return QaReportFormat.ExtractFindings(report);
            }
        }
        return [];
    }

    /// <summary>Finds the factory's open pull request for the ticket a <c>/qa</c> came from, or reads the pull request it came from.</summary>
    [Activity("forgejo_read_qa_context")]
    public async Task<QaContext> ReadQaContextAsync(QaRequest request)
    {
        var token = ActivityExecutionContext.Current.CancellationToken;
        var number = request.OnPullRequest
            ? request.Number
            : await forgejo.FindFactoryPullRequestAsync(request.Owner, request.Repo, request.Number, token);
        if (number is null)
        {
            return new QaContext(null, settings.ForgejoBotLogin);
        }
        return new QaContext(await forgejo.GetPullRequestAsync(request.Owner, request.Repo, number.Value, token), settings.ForgejoBotLogin);
    }

    /// <summary>Asks the reviewers to look again after a revision was pushed. Never fails the run.</summary>
    [Activity("forgejo_request_reviewers")]
    public Task<bool> RequestReviewersAsync(ReviewRequestInput request) =>
        forgejo.RequestReviewAsync(request.Owner, request.Repo, request.PullNumber, request.Reviewers, ActivityExecutionContext.Current.CancellationToken);

    [Activity("forgejo_create_pull_request")]
    public Task<PullRequestInfo> CreatePullRequestAsync(PullRequestRequest request) =>
        forgejo.CreatePullRequestAsync(
            request.Owner, request.Repo, request.Head, request.Base, request.Title, request.Body,
            ActivityExecutionContext.Current.CancellationToken);
}
