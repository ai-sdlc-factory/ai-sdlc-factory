using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

/// <summary>
/// What every factory workflow shares: the private workspace, the conversation with people on the ticket, running
/// one agent, the build loop with the project's checks and the reviewers, and saving unfinished work. A concrete
/// workflow decides which stages come first (<see cref="RunPipelineAsync"/>). The workflow code owns every decision;
/// agents only return a verdict value, which is validated before it is used.
/// </summary>
public abstract partial class FactoryWorkflowBase
{
    protected static readonly TimeSpan WaitForHuman = TimeSpan.FromDays(7);
    /// <summary>How many rounds of questions to a person a stage may use; the project's <c>limits.clarification_rounds</c> or the default.</summary>
    protected int maxClarificationRounds = FactoryDefaults.Standard.ClarificationRounds;

    /// <summary>
    /// How many times a stage may run when it may need to ask a person: one run more than the rounds of questions allowed, because the
    /// last run must answer without asking. A stage's own number of runs is a ceiling that this can only lower.
    /// </summary>
    protected int Rounds(int stageRuns) => Math.Min(stageRuns, maxClarificationRounds + 1);

    /// <summary>How many revisions one pull request may get; checked once the project's settings are read.</summary>
    protected int maxRevisionsPerPullRequest = FactoryDefaults.Standard.RevisionsPerPullRequest;
    protected const int MaxDesignRounds = 3;
    protected const int MaxAgentAttempts = 2;

    protected readonly List<HumanReply> replies = [];
    protected bool ticketClosed;

    // The ticket this run is for, and whether any later stage can still take a human comment: the analyst and the
    // architect read them, the build does not.
    protected WorkRequest? current;
    protected bool commentsCanStillBeUsed = true;
    protected bool tooLateNoticePosted;

    /// <summary>A human comment arrived. Only the analyst and the architect read them; later ones get a notice.</summary>
    protected async Task AcceptHumanReplyAsync(HumanReply reply)
    {
        if (current is not null && !commentsCanStillBeUsed)
        {
            // One notice per run is enough, however many comments follow.
            if (!tooLateNoticePosted)
            {
                tooLateNoticePosted = true;
                await CommentAsync(current, TicketComments.CommentTooLate);
            }
            return;
        }
        replies.Add(reply);
    }

    protected void MarkTicketClosed() => ticketClosed = true;

    /// <summary>
    /// The run every workflow shares: prepare the workspace, read the project's configuration, run the workflow's own
    /// <paramref name="stages"/>, report comments nobody read, save unfinished work, and always remove the checkout.
    /// </summary>
    protected async Task<WorkResult> RunPipelineAsync(
        WorkRequest request, string kind, Func<WorkContext, Task<WorkResult>> stages,
        Func<WorkRequest, WorkspaceRequest>? workspaceFor = null, string? startMessage = null)
    {
        current = request;
        await CommentAsync(request, startMessage ?? $"The factory picked up this {kind} ticket. Preparing a workspace.");
        var workspaceRequest = workspaceFor?.Invoke(request)
            ?? new WorkspaceRequest(request.Owner, request.Repo, request.WorkflowId, $"factory/issue-{request.IssueNumber}");

        try
        {
            WorkspaceInfo workspace;
            try
            {
                workspace = await Workflow.ExecuteActivityAsync(
                    (WorkspaceActivities activities) => activities.PrepareAsync(workspaceRequest),
                    ActivityPolicy.Local(TimeSpan.FromMinutes(5), 3));
            }
            catch (ActivityFailureException error)
            {
                await CommentAsync(request, $"Setting up the workspace failed: {Short(error.InnerException?.Message ?? error.Message)}");
                return new WorkResult(Statuses.SetupFailed);
            }

            var configText = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.ReadProjectConfigAsync(request.WorkflowId),
                ActivityPolicy.Local(TimeSpan.FromMinutes(1)));
            if (!ProjectConfigParser.TryParse(configText, out var project, out var configError))
            {
                await CommentAsync(request, configText is null ? NoConfigMessage : $"`.factory/project.json` cannot be used: {TicketComments.Inert(configError)}. Fix it, then {AskAgain(request)}");
                return new WorkResult(Statuses.NeedsConfig);
            }

            if (project.Warnings is { Count: > 0 } warnings)
            {
                // Said once while it is still among the latest comments, so a retry or a revision does not repeat it.
                await Workflow.ExecuteActivityAsync(
                    (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
                        new CommentRequest(request.Owner, request.Repo, request.IssueNumber, TicketComments.UnknownProjectKeys(warnings))),
                    ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
            }

            var defaults = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.GetDefaultsAsync(),
                ActivityPolicy.Local(TimeSpan.FromMinutes(1)));
            var limits = (project.Limits ?? new ProjectLimits()).Resolve(defaults);
            maxBuildRounds = limits.BuildRounds;
            maxClarificationRounds = limits.ClarificationRounds;
            maxRevisionsPerPullRequest = limits.RevisionsPerPullRequest;
            SetTokenLimits(limits, defaults);
            var extraProtected = (project.ProtectedPaths ?? []).ToList();
            if (project.Verify is { } verify && request.Kind != WorkIds.Qa)
            {
                // The end-to-end tests are written by the /qa run only; the developer must not be able to weaken what judges its work.
                extraProtected.Add(verify.ProtectedPattern);
            }
            if (extraProtected.Count > 0)
            {
                await Workflow.ExecuteActivityAsync(
                    (WorkspaceActivities activities) => activities.SetProtectedPathsAsync(new ProtectedPathsRequest(request.WorkflowId, extraProtected)),
                    ActivityPolicy.Local(TimeSpan.FromMinutes(1)));
            }

            var (image, imageStop) = await PrepareProjectImageAsync(request, project, defaults);
            if (imageStop is not null)
            {
                return imageStop;
            }
            project = project with { Image = image };

            if (project.Record)
            {
                var runId = Workflow.Info.RunId ?? "run";
                recordDirectory = request.Kind switch
                {
                    WorkIds.Revision => RunRecord.DirectoryForRevision(request.IssueNumber, runId),
                    WorkIds.Qa => RunRecord.DirectoryForQa(request.IssueNumber, runId),
                    WorkIds.Update => RunRecord.DirectoryForUpdate(request.IssueNumber, runId),
                    _ => RunRecord.DirectoryFor(request.IssueNumber, runId),
                };
            }

            WorkResult result;
            try
            {
                result = await stages(new WorkContext(request, workspace, project));
            }
            catch (RunStopException stop)
            {
                // The token budget is used up, a daily limit's wait ended with the ticket closed, or the provider's usage limit is reached.
                if (stop.Comment is not null)
                {
                    await CommentAsync(request, stop.Comment);
                }
                result = new WorkResult(stop.Status);
            }
            catch (ActivityFailureException error)
            {
                // Without this the ticket would just go quiet while the run shows as failed in Temporal.
                var infrastructure = InfrastructureFailure.Is(error);
                var reason = infrastructure ? InfrastructureFailure.Describe(error) : error.InnerException?.Message ?? error.Message;
                await CommentAsync(request, $"The factory hit an unexpected error and stopped: {TicketComments.Inert(Short(reason))}{(infrastructure ? TicketComments.RetryHint : "")}");
                result = new WorkResult(infrastructure ? Statuses.InfrastructureFailed : Statuses.Error);
            }
            if (result.Status == Statuses.PrOpened)
            {
                await ClearResumeAsync(request);
            }
            await NoteUnusedCommentsAsync(request, result);
            result = await SaveUnfinishedWorkAsync(request, result);
            result = result with { Usage = TotalUsage };
            await LogUsageAsync(request, result);
            return result;
        }
        finally
        {
            // Runs even if the workflow was cancelled, so checkouts do not pile up on the host.
            await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.CleanupAsync(request.WorkflowId),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(1),
                    CancellationToken = CancellationToken.None,
                });
        }
    }

    /// <summary>
    /// Adds the run to the host's usage log when an agent ran (even one that failed before using any tokens), so there is a total across tickets, days and repositories. Best effort: the log never decides
    /// how a run ends. One attempt, because a second after a lost answer would count the run twice.
    /// </summary>
    private async Task LogUsageAsync(WorkRequest request, WorkResult result)
    {
        // A run whose agents all failed before spending anything is still worth a line: its failures are what the weekly report looks for.
        var stats = Stats;
        if (result.Usage is null && stats.Roles.Count == 0)
        {
            return;
        }
        var usage = result.Usage ?? new TokenUsage(0, 0, 0, 0, 0, 0, 0, 0);
        try
        {
            await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.LogUsageAsync(new UsageLogEntry(
                    request.WorkflowId, Workflow.Info.RunId ?? "", request.Kind, $"{request.Owner}/{request.Repo}", request.IssueNumber, result.Status, usage, stats)),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 1));
        }
        catch (ActivityFailureException)
        {
        }
    }

    /// <summary>
    /// Comments that arrived but were never read by a stage (nothing asked a question afterwards) are reported once at
    /// the end, so a person is not left wondering. A ticket a person closed needs no such reply.
    /// </summary>
    protected async Task NoteUnusedCommentsAsync(WorkRequest request, WorkResult result)
    {
        commentsCanStillBeUsed = false;
        if (replies.Count > 0 && !ticketClosed && result.Status != Statuses.Closed)
        {
            var count = replies.Count;
            replies.Clear();
            await CommentAsync(request, TicketComments.CommentsNotUsed(count));
        }
    }

    // Endings after which code may exist that nobody has seen; a run a person closed, or one that stopped before the build, has none.
    protected static readonly HashSet<string> StoppedWithWork = [Statuses.BudgetExceeded, Statuses.UsageLimit, Statuses.BuildFailed, Statuses.BuildBlocked, Statuses.ReviewFailed, Statuses.PublishFailed, Statuses.Blocked, Statuses.Error, Statuses.InfrastructureFailed];

    // For these the ticket always gets a summary, even when there was nothing to save.
    protected static readonly HashSet<string> AlwaysExplained = [Statuses.BudgetExceeded, Statuses.UsageLimit, Statuses.BuildFailed, Statuses.BuildBlocked, Statuses.ReviewFailed, Statuses.PublishFailed, Statuses.Error];

    /// <summary>
    /// When a run stops without publishing, push what it had written to a work-in-progress branch and say so on the
    /// ticket, with a link to the full record of the run. Saving is best effort: it never replaces the real outcome.
    /// </summary>
    protected async Task<WorkResult> SaveUnfinishedWorkAsync(WorkRequest request, WorkResult result)
    {
        // Already on a branch (only the pull request failed), or nothing that could hold unsaved work.
        if (!StoppedWithWork.Contains(result.Status) || result.WorkBranch is not null)
        {
            return result;
        }

        RescueResult rescue;
        try
        {
            rescue = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.RescueAsync(new RescueRequest(
                    request.WorkflowId,
                    $"WIP: {PullRequestText.Title(request)} (#{request.IssueNumber}), not finished\n\n" +
                    $"Saved by the factory when it stopped ({result.Status}). Not reviewed, and the checks did not all pass.",
                    RecordSpec(request, $"Stopped before finishing ({result.Status}). The work so far is on this work-in-progress branch; it was not reviewed and the checks did not all pass."))),
                ActivityPolicy.Local(TimeSpan.FromMinutes(5), 2));
        }
        catch (ActivityFailureException error)
        {
            await CommentAsync(request, $"The factory stopped ({result.Status}) and could not save the work so far: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))}");
            return result;
        }

        if (rescue.Saved || AlwaysExplained.Contains(result.Status))
        {
            await CommentAsync(request, TicketComments.Stopped(result.Status, rescue));
        }
        return rescue.Saved ? result with { WorkBranch = rescue.Branch } : result;
    }

    /// <summary>How a person starts this kind of run again, for the messages that say "fix it, then ...".</summary>
    protected static string AskAgain(WorkRequest request) => request.Kind switch
    {
        WorkIds.Revision => "ask again with `/fix`.",
        WorkIds.Qa => "comment `/qa` again.",
        WorkIds.Update => "comment `/update` again.",
        _ => "close and reopen this ticket.",
    };

    protected const string NoConfigMessage =
        "This repository has no `.factory/project.json`, so I do not know which sandbox image or checks to use. " +
        "Add one on the default branch, for example:\n\n```json\n" +
        "{\n  \"image\": \"python\",\n  \"gates\": [\n    { \"name\": \"test\", \"command\": [\"python\", \"-m\", \"pytest\", \"-q\"] }\n  ]\n}\n```\n\n" +
        "Then close and reopen this ticket.";

    /// <summary>
    /// The network each agent role gets: the developer may also fetch packages, every other agent can reach only
    /// the model provider. Which hosts a profile allows is decided in egress/profiles.json, not here.
    /// </summary>
    public static string NetworkFor(string role) => role is "developer" or "reproducer" or "pr-resolver" or "merge-resolver" or "e2e-tester" ? SandboxNetworks.Developer : SandboxNetworks.Model;

    protected const string NotSignedInMessage =
        "OpenCode is not signed in, so I cannot run the agents. Run the `opencode-login` service, then close and reopen this ticket.";

    protected void DrainReplies(List<string> answers)
    {
        foreach (var reply in replies)
        {
            answers.Add(reply.Body);
        }
        replies.Clear();
    }

    /// <summary>Keeps repository- or model-supplied text short and free of characters that break the comment's formatting.</summary>
    protected static string Short(string text)
    {
        var flat = text.Replace('`', '\'').Replace('\n', ' ').Trim();
        return flat.Length > 200 ? flat[..200] + "..." : flat;
    }

    protected static Task CommentAsync(WorkRequest request, string body) =>
        Workflow.ExecuteActivityAsync(
            (ForgejoActivities activities) => activities.PostIssueCommentAsync(
                new CommentRequest(request.Owner, request.Repo, request.IssueNumber, body)),
            ActivityPolicy.Local(TimeSpan.FromSeconds(30), 5));
}
