using Temporalio.Activities;

namespace DevFoundry.Controller;

public sealed record CommentRequest(string Owner, string Repo, int IssueNumber, string Body);

public sealed record CreateIssueRequest(string Owner, string Repo, string Title, string Body, IReadOnlyList<string> Labels);

public sealed record ReviewRequestInput(string Owner, string Repo, int PullNumber, IReadOnlyList<string> Reviewers);


public sealed record PullRequestRequest(string Owner, string Repo, string Head, string Base, string Title, string Body);

/// <summary>A ticket, by number, whose open pull request of the factory is wanted.</summary>
public sealed record TicketReference(string Owner, string Repo, int Number);

public sealed class GitHostActivities(IGitHost host, WorkerSettings settings)
{
    /// <summary>The number of the factory's newest open pull request for the ticket, or null when it has none.</summary>
    [Activity("githost_find_factory_pull_request")]
    public Task<int?> FindFactoryPullRequestAsync(TicketReference ticket) =>
        host.FindFactoryPullRequestAsync(ticket.Owner, ticket.Repo, ticket.Number, ActivityExecutionContext.Current.CancellationToken);

    /// <summary>
    /// For the weekly report: what became of the pull requests the factory opened in a week (merged, closed without merging, still open). Read-only, and only numbers and states
    /// come back. At most <see cref="MaxRepositories"/> repositories, the ones the usage log names.
    /// </summary>
    [Activity("githost_pull_outcomes")]
    public async Task<IReadOnlyList<PullOutcome>> ReadPullOutcomesAsync(PullOutcomeRequest request)
    {
        var token = ActivityExecutionContext.Current.CancellationToken;
        var outcomes = new List<PullOutcome>();
        foreach (var repository in request.Scope.Repositories.Distinct().Take(MaxRepositories))
        {
            if (repository.Split('/') is not [var owner, var repo])
            {
                continue;
            }
            var pulls = await host.ListAllPullRequestsAsync(owner, repo, token);
            outcomes.AddRange(PullOutcomes.Build(repository, pulls, request.Scope, settings.ForgejoBotLogin, request.From, request.To));
        }
        return outcomes;
    }

    private const int MaxRepositories = 25;

    [Activity("githost_post_issue_comment")]
    public Task PostIssueCommentAsync(CommentRequest request) =>
        host.PostIssueCommentAsync(
            request.Owner, request.Repo, request.IssueNumber, request.Body,
            ActivityExecutionContext.Current.CancellationToken);

    [Activity("githost_post_notice_once")]
    public Task<bool> PostNoticeOnceAsync(CommentRequest request) =>
        host.PostNoticeOnceAsync(
            request.Owner, request.Repo, request.IssueNumber, request.Body,
            ActivityExecutionContext.Current.CancellationToken);

    [Activity("githost_post_notice_unique")]
    public Task<bool> PostNoticeUniqueAsync(CommentRequest request) =>
        host.PostNoticeUniqueAsync(
            request.Owner, request.Repo, request.IssueNumber, request.Body,
            ActivityExecutionContext.Current.CancellationToken);

    [Activity("githost_create_issue")]
    public Task<IssueInfo> CreateIssueAsync(CreateIssueRequest request) =>
        host.CreateIssueAsync(
            request.Owner, request.Repo, request.Title, request.Body, request.Labels,
            ActivityExecutionContext.Current.CancellationToken);

    /// <summary>Reads what is needed to decide whether to revise a pull request, and what the reviewer asked for.</summary>
    [Activity("githost_read_revision_context")]
    public async Task<RevisionContext> ReadRevisionContextAsync(RevisionRequest request)
    {
        var token = ActivityExecutionContext.Current.CancellationToken;
        var bot = settings.ForgejoBotLogin;
        var pull = await host.GetPullRequestAsync(request.Owner, request.Repo, request.PullNumber, token);
        var history = await host.GetRevisionHistoryAsync(request.Owner, request.Repo, request.PullNumber, token);
        var reviews = await host.GetUnaddressedReviewsAsync(request.Owner, request.Repo, request.PullNumber, bot, history.LastAt, token);
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
            var bodies = await host.RecentCommentBodiesAsync(request.Owner, request.Repo, number, 50, token);
            if (bodies.LastOrDefault(body => body.Contains(QaReportFormat.Marker, StringComparison.Ordinal)) is { } report)
            {
                return QaReportFormat.ExtractFindings(report);
            }
        }
        return [];
    }

    /// <summary>Finds the factory's open pull request for the ticket a <c>/qa</c> came from, or reads the pull request it came from.</summary>
    [Activity("githost_read_qa_context")]
    public async Task<QaContext> ReadQaContextAsync(QaRequest request)
    {
        var token = ActivityExecutionContext.Current.CancellationToken;
        var number = request.OnPullRequest
            ? request.Number
            : await host.FindFactoryPullRequestAsync(request.Owner, request.Repo, request.Number, token);
        if (number is null)
        {
            return new QaContext(null, settings.ForgejoBotLogin);
        }
        var pull = await host.GetPullRequestAsync(request.Owner, request.Repo, number.Value, token);
        return new QaContext(pull, settings.ForgejoBotLogin, await TryGetChangesAsync(request.Owner, request.Repo, number.Value, token));
    }

    /// <summary>What the pull request changes, for the tester. A tester that is not told is only less well informed, so a failure here never stops the run.</summary>
    private async Task<PullChanges?> TryGetChangesAsync(string owner, string repo, int number, CancellationToken token)
    {
        try
        {
            return await host.GetPullChangesAsync(owner, repo, number, token);
        }
        catch (Exception error) when (error is Temporalio.Exceptions.ApplicationFailureException or HttpRequestException || (error is TaskCanceledException && !token.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Finds the factory's open pull request for the ticket an <c>/update</c> came from, or reads the pull request it came from.</summary>
    [Activity("githost_read_update_context")]
    public async Task<UpdateContext> ReadUpdateContextAsync(UpdateRequest request)
    {
        var token = ActivityExecutionContext.Current.CancellationToken;
        var number = request.OnPullRequest
            ? request.Number
            : await host.FindFactoryPullRequestAsync(request.Owner, request.Repo, request.Number, token);
        if (number is null)
        {
            return new UpdateContext(null, settings.ForgejoBotLogin);
        }
        return new UpdateContext(await host.GetPullRequestAsync(request.Owner, request.Repo, number.Value, token), settings.ForgejoBotLogin);
    }

    /// <summary>Reads the pull request a <c>/review</c> is for, and what it changes.</summary>
    [Activity("githost_read_review_context")]
    public async Task<ReviewContext> ReadReviewContextAsync(PullReviewRequest request)
    {
        var token = ActivityExecutionContext.Current.CancellationToken;
        var pull = await host.GetPullRequestAsync(request.Owner, request.Repo, request.Number, token);
        return new ReviewContext(pull, await TryGetChangesAsync(request.Owner, request.Repo, request.Number, token));
    }

    /// <summary>Asks the reviewers to look again after a revision was pushed. Never fails the run.</summary>
    [Activity("githost_request_reviewers")]
    public Task<bool> RequestReviewersAsync(ReviewRequestInput request) =>
        host.RequestReviewAsync(request.Owner, request.Repo, request.PullNumber, request.Reviewers, ActivityExecutionContext.Current.CancellationToken);

    [Activity("githost_create_pull_request")]
    public Task<PullRequestInfo> CreatePullRequestAsync(PullRequestRequest request) =>
        host.CreatePullRequestAsync(
            request.Owner, request.Repo, request.Head, request.Base, request.Title, request.Body,
            ActivityExecutionContext.Current.CancellationToken);
}
