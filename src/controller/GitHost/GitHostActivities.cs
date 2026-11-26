using Temporalio.Activities;

namespace AiSdlcFactory.Controller;

public sealed record CommentRequest(string Owner, string Repo, int IssueNumber, string Body);

public sealed record CreateIssueRequest(string Owner, string Repo, string Title, string Body, IReadOnlyList<string> Labels);

public sealed record ReviewRequestInput(string Owner, string Repo, int PullNumber, IReadOnlyList<string> Reviewers);


public sealed record PullRequestRequest(string Owner, string Repo, string Head, string Base, string Title, string Body);

/// <summary>A ticket, by number, whose open pull request of the factory is wanted.</summary>
public sealed record TicketReference(string Owner, string Repo, int Number);

/// <summary>Whose command to check: the host, the repository (the factory's qualified owner), the login the host reported for the person, and the commands the comment carries (see <see cref="CommandNameReader"/>).</summary>
public sealed record CommandCheck(string Host, string Owner, string Repo, string Login, IReadOnlyList<string>? Commands = null);

public sealed class GitHostActivities(IGitHost host, WorkerSettings settings, PermissionCache? permissions = null, ProjectRuleCache? projectRules = null, TeamCache? teams = null)
{
    private readonly PermissionCache cache = permissions ?? new PermissionCache(TimeSpan.FromSeconds(settings.PermissionCacheSeconds));
    private readonly ProjectRuleCache projectCache = projectRules ?? new ProjectRuleCache(TimeSpan.FromSeconds(settings.PermissionCacheSeconds));
    private readonly TeamCache teamCache = teams ?? new TeamCache(TimeSpan.FromSeconds(settings.PermissionCacheSeconds));

    private const string ProjectFile = ".factory/project.json";

    /// <summary>The number of the factory's newest open pull request for the ticket, or null when it has none.</summary>
    [Activity("githost_find_factory_pull_request")]
    public Task<int?> FindFactoryPullRequestAsync(TicketReference ticket) =>
        host.FindFactoryPullRequestAsync(ticket.Owner, ticket.Repo, ticket.Number, ActivityExecutionContext.Current.CancellationToken);

    /// <summary>Where the factory's newest pull request for a ticket stands (open, merged, closed without merging, or none). Read-only; used to say something useful to a comment nobody is listening to.</summary>
    [Activity("githost_ticket_pull_status")]
    public async Task<TicketPullStatus> ReadTicketPullStatusAsync(TicketReference ticket)
    {
        var pulls = await host.ListAllPullRequestsAsync(ticket.Owner, ticket.Repo, ActivityExecutionContext.Current.CancellationToken);
        return TicketPullStatus.Of(ticket.Number, pulls, settings.BotLoginFor(ticket.Owner));
    }

    /// <summary>Whether a plain comment on a pull request is on one of the factory's own open pull requests, written by a person (not the bot): the only place the hint about commands is said.</summary>
    [Activity("githost_is_hint_target")]
    public async Task<bool> IsHintTargetAsync(PullHintRequest request)
    {
        var bot = settings.BotLoginFor(request.Owner);
        if (string.Equals(request.Author, bot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var pull = await host.GetPullRequestAsync(request.Owner, request.Repo, request.PullNumber, ActivityExecutionContext.Current.CancellationToken);
        return RevisionRules.IsOpenFactoryPull(pull, bot);
    }

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
            outcomes.AddRange(PullOutcomes.Build(repository, pulls, request.Scope, settings.BotLoginFor(owner), request.From, request.To));
        }
        return outcomes;
    }

    private const int MaxRepositories = 25;

    /// <summary>How a host's tickets say which workflow to run, read at routing time so a replayed delivery sees the rules it was routed with.</summary>
    [Activity("githost_triggers")]
    public Task<TriggerRules> ReadTriggersAsync(string hostName) =>
        Task.FromResult(settings.HostNamed(hostName).Triggers ?? TriggerRules.Default);

    /// <summary>
    /// Whether a person may steer the factory on a repository, by the operator's rule for it (<c>commands</c> in <c>FACTORY_HOSTS</c>): the rule, then the rule of each command the comment carries,
    /// then, where the operator allowed it, what the repository's project file tightens. With no rule, or a rule that names the person, the host is not asked at all; otherwise it is, and a clear
    /// answer is kept for a while. A host that cannot say, or fails, makes the answer no.
    /// </summary>
    [Activity("githost_check_command")]
    public async Task<CommandVerdict> CheckCommandAsync(CommandCheck check)
    {
        var rule = (settings.HostNamed(check.Host).Commands ?? HostCommandRules.Open).For(RepoNames.Split(check.Owner).Owner, check.Repo);
        if (rule.IsOpen)
        {
            return CommandVerdict.Yes;
        }
        var login = check.Login.Trim();
        var commands = check.Commands ?? [];
        var token = ActivityExecutionContext.Current.CancellationToken;

        ProjectCommandRule? project = null;
        if (rule.Project)
        {
            try
            {
                project = await ReadProjectRuleAsync(check.Owner, check.Repo, token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The repository may have asked for more than the operator did, and the file cannot be read: no, for now (not kept, so the next command reads it again).
                ActivityExecutionContext.Current.Logger.LogWarning("Could not read the project file of {Repository}: {Error}", $"{check.Owner}/{check.Repo}", error.GetType().Name);
                return new CommandVerdict(false, CommandRefusal.CouldNotCheck, "", rule.Silent);
            }
        }

        HostPermission? permission = null;
        if (CommandPolicy.NeedsPermission(rule, login, commands, project))
        {
            if (cache.TryGet(check.Owner, check.Repo, login, out var kept))
            {
                permission = kept;
            }
            else
            {
                try
                {
                    permission = await host.GetPermissionAsync(check.Owner, check.Repo, login, token);
                    cache.Keep(check.Owner, check.Repo, login, permission.Value);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Not kept, and the person is refused for now: the next command asks again. Only the kind of failure is logged, never what the host sent back.
                    ActivityExecutionContext.Current.Logger.LogWarning("Could not look up a person's access to {Repository}: {Error}", $"{check.Owner}/{check.Repo}", error.GetType().Name);
                }
            }
        }
        return CommandPolicy.Decide(rule, login, permission, commands, project);
    }

    /// <summary>
    /// Whether a person may START a run of this kind on a repository, by the operator's <c>start</c> rule for it. With no rule, or a rule that does not cover this kind, the host is not asked at all;
    /// otherwise it is asked only what the rule needs (the person's access, and whether they are in the teams it names, and only while that could still change the answer), and a clear answer is kept
    /// for a while. A host that cannot say, or fails, makes the answer no, and the verdict says that it could not be checked.
    /// </summary>
    [Activity("githost_check_start")]
    public async Task<StartVerdict> CheckStartAsync(StartCheck check)
    {
        var rule = (settings.HostNamed(check.Host).Start ?? HostStartRules.Open).For(RepoNames.Split(check.Owner).Owner, check.Repo);
        if (!rule.Covers(check.Kind))
        {
            return StartVerdict.Yes;
        }
        var login = check.Login.Trim();
        var token = ActivityExecutionContext.Current.CancellationToken;

        HostPermission? permission = null;
        if (StartPolicy.NeedsPermission(rule, login))
        {
            if (cache.TryGet(check.Owner, check.Repo, login, out var kept))
            {
                permission = kept;
            }
            else
            {
                try
                {
                    permission = await host.GetPermissionAsync(check.Owner, check.Repo, login, token);
                    cache.Keep(check.Owner, check.Repo, login, permission.Value);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    ActivityExecutionContext.Current.Logger.LogWarning("Could not look up a person's access to {Repository}: {Error}", $"{check.Owner}/{check.Repo}", error.GetType().Name);
                }
            }
        }

        // The person's own team answers are looked for only while they could still let them in; one that says yes ends the asking.
        var membership = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in StartPolicy.TeamsToAsk(rule, login, permission))
        {
            if (teamCache.TryGet(check.Host, team, login, out var member))
            {
                membership[team] = member;
            }
            else
            {
                try
                {
                    if (await host.IsTeamMemberAsync(check.Owner, team, login, token) is { } answer)
                    {
                        membership[team] = answer;
                        teamCache.Keep(check.Host, team, login, answer);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    ActivityExecutionContext.Current.Logger.LogWarning("Could not look up a team of {Repository}: {Error}", $"{check.Owner}/{check.Repo}", error.GetType().Name);
                }
            }
            if (membership.TryGetValue(team, out var isMember) && isMember)
            {
                break;
            }
        }
        return StartPolicy.Decide(rule, check.Kind, login, permission, membership);
    }

    /// <summary>What the repository's <c>.factory/project.json</c> says about who may steer, kept for a while. No file, no key, or a key that cannot be used says nothing (the run's own warning names it).</summary>
    private async Task<ProjectCommandRule> ReadProjectRuleAsync(string owner, string repo, CancellationToken token)
    {
        if (projectCache.TryGet(owner, repo, out var kept))
        {
            return kept;
        }
        var text = await host.ReadFileAsync(owner, repo, ProjectFile, token);
        var rule = ProjectCommandRule.None;
        if (text is not null)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && document.RootElement.TryGetProperty("commands", out var commands))
                {
                    rule = ProjectCommandRule.Read(commands, "commands in .factory/project.json");
                }
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
            {
                ActivityExecutionContext.Current.Logger.LogWarning("The project file of {Repository} says nothing usable about who may steer: {Error}", $"{owner}/{repo}", error.GetType().Name);
            }
        }
        projectCache.Keep(owner, repo, rule);
        return rule;
    }

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
        var bot = settings.BotLoginFor(request.Owner);
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
            return new QaContext(null, settings.BotLoginFor(request.Owner));
        }
        var pull = await host.GetPullRequestAsync(request.Owner, request.Repo, number.Value, token);
        return new QaContext(pull, settings.BotLoginFor(request.Owner), await TryGetChangesAsync(request.Owner, request.Repo, number.Value, token));
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
            return new UpdateContext(null, settings.BotLoginFor(request.Owner));
        }
        return new UpdateContext(await host.GetPullRequestAsync(request.Owner, request.Repo, number.Value, token), settings.BotLoginFor(request.Owner));
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
