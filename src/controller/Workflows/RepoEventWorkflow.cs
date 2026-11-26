using Temporalio.Api.Enums.V1;
using ParentClosePolicy = Temporalio.Workflows.ParentClosePolicy;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AiSdlcFactory.Controller;

[Workflow("RepoEventWorkflow")]
public sealed class RepoEventWorkflow
{
    /// <summary>
    /// Preserves the delivery in workflow history, reads it through its host's adapter, produces a small receipt, and routes tickets to their own long-running
    /// workflow. Webhook contents are data; this workflow executes no supplied commands.
    /// </summary>
    [WorkflowRun]
    public async Task<EventReceipt> RunAsync(WebhookDelivery input)
    {
        var adapter = WebhookAdapters.Find(input.Adapter)
            ?? throw new ApplicationFailureException($"No webhook adapter for the host type \"{input.Adapter}\".", nonRetryable: true);
        var repoEvent = adapter.Normalise(input);
        // The host's triggers come from an activity (the settings are not workflow code's to read), so a replayed delivery is routed by the rules it first met.
        // Only a ticket event is decided by them; a push, a review or a pull request comment needs no lookup.
        var triggers = repoEvent.Kind is RepoEventKind.TicketOpened or RepoEventKind.TicketReopened or RepoEventKind.TicketClosed or RepoEventKind.Other
            || (repoEvent.Kind == RepoEventKind.Commented && !repoEvent.IsPullRequest)
            ? await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.ReadTriggersAsync(input.Host),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3))
            : TriggerRules.Default;
        var decision = EventRouter.Decide(repoEvent, triggers);
        if (!await MayProceedAsync(input.Host, repoEvent, decision))
        {
            return new EventReceipt(input.DeliveryId, input.Event, repoEvent.Repository, repoEvent.Action, repoEvent.Reference);
        }
        if (!await MayStartAsync(input.Host, repoEvent, decision))
        {
            return new EventReceipt(input.DeliveryId, input.Event, repoEvent.Repository, repoEvent.Action, repoEvent.Reference);
        }
        await RouteAsync(decision);
        return new EventReceipt(input.DeliveryId, input.Event, repoEvent.Repository, repoEvent.Action, repoEvent.Reference);
    }

    /// <summary>
    /// The operator's rule of who may START a run (<c>start</c> in <c>FACTORY_HOSTS</c>), applied before one starts: by a label, by opening or reopening a ticket that carries one, or by <c>/factory</c> or
    /// <c>/retry</c>. The person judged is the one who set it off. A person it refuses gets one short comment (unless the rule says to stay silent) and nothing starts. With no rule the activity
    /// answers yes without asking the host anything. Separate from <see cref="MayProceedAsync"/>, which judges who may steer: a person may be allowed one and not the other.
    /// </summary>
    private static async Task<bool> MayStartAsync(string host, RepoEvent input, RoutingDecision decision)
    {
        if (decision.Action is not (RoutingAction.StartFeature or RoutingAction.RetryFeature) || decision.Feature is not { } ticket)
        {
            return true;
        }
        var login = input.Kind == RepoEventKind.Commented ? input.CommentAuthor : input.Actor;
        var verdict = await Workflow.ExecuteActivityAsync(
            (GitHostActivities activities) => activities.CheckStartAsync(new StartCheck(host, input.Owner, input.Repo, login, ticket.Kind)),
            ActivityPolicy.Local(TimeSpan.FromSeconds(60), 3));
        if (verdict.Allowed)
        {
            return true;
        }
        if (!verdict.Silent)
        {
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (GitHostActivities activities) => activities.PostNoticeUniqueAsync(new CommentRequest(input.Owner, input.Repo, input.Number, TicketComments.StartRefused(verdict))),
                    ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
            }
            catch (ActivityFailureException)
            {
                // Best effort: nothing starts either way.
            }
        }
        return false;
    }

    /// <summary>Whether a person is steering the factory: a command, an answer to an agent, or a review that asks for a revision. A label that starts a run is the host's to limit, not ours.</summary>
    private static bool IsSteering(RepoEvent input, RoutingDecision decision) => decision.Action switch
    {
        RoutingAction.RevisePullRequest or RoutingAction.RetryFeature or RoutingAction.RunQa or RoutingAction.ConfirmQa or RoutingAction.UpdatePullRequest
            or RoutingAction.ReviewPullRequest or RoutingAction.AskQuestion or RoutingAction.RunSetup or RoutingAction.ReplyToFeature => true,
        RoutingAction.StartFeature => input.Kind == RepoEventKind.Commented, // `/factory <kind>`
        _ => false,
    };

    /// <summary>
    /// The operator's rule of who may steer the factory, applied before anything is started. A person it refuses gets one short comment (unless the rule says to stay silent) and nothing runs.
    /// With no rule the activity answers yes without asking the host anything.
    /// </summary>
    private static async Task<bool> MayProceedAsync(string host, RepoEvent input, RoutingDecision decision)
    {
        if (!IsSteering(input, decision))
        {
            return true;
        }
        var login = input.Kind == RepoEventKind.ChangesRequested ? input.Actor : input.CommentAuthor;
        var verdict = await Workflow.ExecuteActivityAsync(
            (GitHostActivities activities) => activities.CheckCommandAsync(new CommandCheck(host, input.Owner, input.Repo, login, CommandNameReader.Of(input, decision))),
            ActivityPolicy.Local(TimeSpan.FromSeconds(60), 3));
        if (verdict.Allowed)
        {
            return true;
        }
        if (!verdict.Silent)
        {
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (GitHostActivities activities) => activities.PostNoticeUniqueAsync(new CommentRequest(input.Owner, input.Repo, input.Number, TicketComments.CommandRefused(verdict))),
                    ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
            }
            catch (ActivityFailureException)
            {
                // Best effort: the command is refused either way.
            }
        }
        return false;
    }

    private static Task RouteAsync(RoutingDecision decision)
    {
        return decision.Action switch
        {
            RoutingAction.StartFeature => StartFromTicketEventAsync(decision),
            RoutingAction.RevisePullRequest => RevisePullRequestAsync(decision.Revision!),
            RoutingAction.RetryFeature => RetryTicketAsync(decision),
            RoutingAction.RunQa => StartQaAsync(decision.Qa!),
            RoutingAction.ConfirmQa => ConfirmQaAsync(decision.Qa!),
            RoutingAction.UpdatePullRequest => StartUpdateAsync(decision.Update!),
            RoutingAction.ReviewPullRequest => StartReviewAsync(decision.Review!),
            RoutingAction.AskQuestion => StartAskAsync(decision.Ask!),
            RoutingAction.RunSetup => StartSetupAsync(decision.Setup!),
            RoutingAction.ReplyToFeature => ReplyToTicketAsync(decision),
            RoutingAction.HintPullRequest => HintPullRequestAsync(decision.Hint!),
            RoutingAction.CloseFeature => CloseTicketAsync(decision.Feature!),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>Starts the incident, bug or feature workflow for a ticket; the workflow outlives this short routing run.</summary>
    private static async Task<bool> StartTicketWorkflowAsync(RoutingDecision decision)
    {
        var feature = decision.Feature!;
        try
        {
            var options = new ChildWorkflowOptions
            {
                Id = feature.WorkflowId,
                ParentClosePolicy = ParentClosePolicy.Abandon,
                IdReusePolicy = decision.Restart ? WorkflowIdReusePolicy.AllowDuplicate : WorkflowIdReusePolicy.RejectDuplicate,
            };
            if (feature.IsIncident)
            {
                await Workflow.StartChildWorkflowAsync((IncidentWorkflow workflow) => workflow.RunAsync(feature), options);
            }
            else if (feature.IsPlan)
            {
                await Workflow.StartChildWorkflowAsync((PlanWorkflow workflow) => workflow.RunAsync(feature), options);
            }
            else if (feature.IsBug)
            {
                await Workflow.StartChildWorkflowAsync((BugWorkflow workflow) => workflow.RunAsync(feature), options);
            }
            else if (feature.IsDocs)
            {
                await Workflow.StartChildWorkflowAsync((DocsWorkflow workflow) => workflow.RunAsync(feature), options);
            }
            else
            {
                await Workflow.StartChildWorkflowAsync((FeatureWorkflow workflow) => workflow.RunAsync(feature), options);
            }
            return true;
        }
        catch (WorkflowAlreadyStartedException)
        {
            // Relabeling or editing a ticket that already has a workflow is not an error.
            return false;
        }
    }

    /// <summary>
    /// A ticket was opened, labelled or reopened. A reopened ticket whose earlier run is still stopping (it was closed a moment ago, and a run only
    /// notices that between its steps) waits for that run to end and then starts a new one, instead of the reopening being lost.
    /// </summary>
    private static async Task StartFromTicketEventAsync(RoutingDecision decision)
    {
        if (await StartTicketWorkflowAsync(decision) || !decision.Restart)
        {
            return;
        }
        for (var attempt = 1; attempt <= MaxReopenWaits; attempt++)
        {
            await Workflow.DelayAsync(TimeSpan.FromSeconds(Math.Min(15, 1 << Math.Min(attempt - 1, 4))));
            if (await StartTicketWorkflowAsync(decision))
            {
                return;
            }
        }
        var feature = decision.Feature!;
        await Workflow.ExecuteActivityAsync(
            (GitHostActivities activities) => activities.PostNoticeOnceAsync(
                new CommentRequest(feature.Owner, feature.Repo, feature.IssueNumber, TicketComments.ReopenStillStopping)),
            ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
    }

    /// <summary>About 30 minutes: longer than the longest step a closed run can be in the middle of.</summary>
    private const int MaxReopenWaits = 120;

    /// <summary>The factory's open pull request for a ticket, or null when it has none (or the git host cannot say).</summary>
    private static async Task<int?> FindOpenPullRequestAsync(string owner, string repo, int ticket)
    {
        try
        {
            return await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.FindFactoryPullRequestAsync(new TicketReference(owner, repo, ticket)),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
        }
        catch (ActivityFailureException)
        {
            return null;
        }
    }

    /// <summary>`/retry`: starts the ticket's run again when the last one has finished; while one is running, or the ticket has an open pull request, says so once.</summary>
    private static async Task RetryTicketAsync(RoutingDecision decision)
    {
        var feature = decision.Feature!;
        // A plain /retry carries on the earlier run, so a pull request that run already opened would get a twin. /retry restart is the person's explicit choice.
        if (feature.Resume && await FindOpenPullRequestAsync(feature.Owner, feature.Repo, feature.IssueNumber) is { } pull)
        {
            await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.PostNoticeOnceAsync(
                    new CommentRequest(feature.Owner, feature.Repo, feature.IssueNumber, TicketComments.RetryHasPullRequest(pull))),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
            return;
        }
        if (await StartTicketWorkflowAsync(decision))
        {
            return;
        }
        await Workflow.ExecuteActivityAsync(
            (GitHostActivities activities) => activities.PostNoticeOnceAsync(
                new CommentRequest(feature.Owner, feature.Repo, feature.IssueNumber, TicketComments.RetryWhileRunning)),
            ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
    }

    /// <summary>`/qa`: starts a QA run; a second one for the same ticket or pull request while one is running gets a notice.</summary>
    private static async Task StartQaAsync(QaRequest qa)
    {
        try
        {
            await Workflow.StartChildWorkflowAsync(
                (QaWorkflow workflow) => workflow.RunAsync(qa),
                new ChildWorkflowOptions
                {
                    Id = qa.WorkflowId,
                    ParentClosePolicy = ParentClosePolicy.Abandon,
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                });
        }
        catch (WorkflowAlreadyStartedException)
        {
            await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.PostNoticeOnceAsync(
                    new CommentRequest(qa.Owner, qa.Repo, qa.Number, TicketComments.QaBusy)),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
        }
    }

    /// <summary>`/qa go`: the go-ahead for a QA run that waits for one. When nothing waits, the person is told.</summary>
    private static async Task ConfirmQaAsync(QaRequest qa)
    {
        if (!await SignalAsync<QaWorkflow>(qa.WorkflowId, workflow => workflow.OnGoAsync()))
        {
            await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.PostNoticeOnceAsync(
                    new CommentRequest(qa.Owner, qa.Repo, qa.Number, TicketComments.QaNothingToConfirm)),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
        }
    }

    /// <summary>`/update`: merges the base branch into the pull request's branch; a second one for the same ticket or pull request while one is running gets a notice.</summary>
    private static async Task StartUpdateAsync(UpdateRequest update)
    {
        try
        {
            await Workflow.StartChildWorkflowAsync(
                (PullRequestUpdateWorkflow workflow) => workflow.RunAsync(update),
                new ChildWorkflowOptions
                {
                    Id = update.WorkflowId,
                    ParentClosePolicy = ParentClosePolicy.Abandon,
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                });
        }
        catch (WorkflowAlreadyStartedException)
        {
            await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.PostNoticeOnceAsync(
                    new CommentRequest(update.Owner, update.Repo, update.Number, TicketComments.UpdateBusy)),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
        }
    }

    /// <summary>`/review`: reviews a pull request (read only); a second one for the same pull request while one is running gets a notice.</summary>
    private static async Task StartReviewAsync(PullReviewRequest review)
    {
        try
        {
            await Workflow.StartChildWorkflowAsync(
                (PullRequestReviewWorkflow workflow) => workflow.RunAsync(review),
                new ChildWorkflowOptions
                {
                    Id = review.WorkflowId,
                    ParentClosePolicy = ParentClosePolicy.Abandon,
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                });
        }
        catch (WorkflowAlreadyStartedException)
        {
            await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.PostNoticeOnceAsync(
                    new CommentRequest(review.Owner, review.Repo, review.Number, TicketComments.PullReviewBusy)),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
        }
    }

    /// <summary>`/ask`: answers a question about the repository (read only); a second one for the same ticket or pull request while one is running gets a notice.</summary>
    private static async Task StartAskAsync(AskRequest ask)
    {
        try
        {
            await Workflow.StartChildWorkflowAsync(
                (AskWorkflow workflow) => workflow.RunAsync(ask),
                new ChildWorkflowOptions
                {
                    Id = ask.WorkflowId,
                    ParentClosePolicy = ParentClosePolicy.Abandon,
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                });
        }
        catch (WorkflowAlreadyStartedException)
        {
            await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.PostNoticeOnceAsync(
                    new CommentRequest(ask.Owner, ask.Repo, ask.Number, AskComments.Busy)),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
        }
    }

    /// <summary>`/setup`: proposes a settings file for the repository; a second one for the same ticket while one is running gets a notice.</summary>
    private static async Task StartSetupAsync(WorkRequest setup)
    {
        try
        {
            await Workflow.StartChildWorkflowAsync(
                (SetupWorkflow workflow) => workflow.RunAsync(setup),
                new ChildWorkflowOptions
                {
                    Id = setup.WorkflowId,
                    ParentClosePolicy = ParentClosePolicy.Abandon,
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                });
        }
        catch (WorkflowAlreadyStartedException)
        {
            await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.PostNoticeOnceAsync(
                    new CommentRequest(setup.Owner, setup.Repo, setup.IssueNumber, SetupComments.Busy)),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
        }
    }

    private static async Task RevisePullRequestAsync(RevisionRequest revision)
    {
        try
        {
            // One revision at a time per pull request; a finished one may be followed by another.
            await Workflow.StartChildWorkflowAsync(
                (PullRequestRevisionWorkflow workflow) => workflow.RunAsync(revision),
                new ChildWorkflowOptions
                {
                    Id = revision.WorkflowId,
                    ParentClosePolicy = ParentClosePolicy.Abandon,
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                });
        }
        catch (WorkflowAlreadyStartedException)
        {
            // Not lost: the running revision is told, and starts a follow-up when it is done.
            var queued = await SignalAsync<PullRequestRevisionWorkflow>(revision.WorkflowId, workflow => workflow.OnMoreFeedbackAsync(revision));
            var busy = new CommentRequest(revision.Owner, revision.Repo, revision.PullNumber, queued ? TicketComments.RevisionQueued : TicketComments.RevisionBusy);
            await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.PostNoticeOnceAsync(busy),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
        }
    }

    private static async Task ReplyToTicketAsync(RoutingDecision decision)
    {
        var feature = decision.Feature!;
        var delivered = feature.IsIncident
            ? await SignalAsync<IncidentWorkflow>(feature.WorkflowId, workflow => workflow.OnHumanReplyAsync(decision.Reply!))
            : feature.IsDocs
                ? await SignalAsync<DocsWorkflow>(feature.WorkflowId, workflow => workflow.OnHumanReplyAsync(decision.Reply!))
                : feature.IsPlan
                ? await SignalAsync<PlanWorkflow>(feature.WorkflowId, workflow => workflow.OnHumanReplyAsync(decision.Reply!))
                : feature.IsBug
                ? await SignalAsync<BugWorkflow>(feature.WorkflowId, workflow => workflow.OnHumanReplyAsync(decision.Reply!))
                : await SignalAsync<FeatureWorkflow>(feature.WorkflowId, workflow => workflow.OnHumanReplyAsync(decision.Reply!));
        if (!delivered)
        {
            // Nobody is listening: the run finished, or never started. Say so instead of staying silent, and say what to do given where the ticket's pull request stands.
            var pull = await ReadTicketPullStatusAsync(feature.Owner, feature.Repo, feature.IssueNumber);
            await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.PostNoticeOnceAsync(
                    new CommentRequest(feature.Owner, feature.Repo, feature.IssueNumber, TicketComments.NoRunActiveFor(pull))),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
        }
    }

    /// <summary>The state of the factory's newest pull request for a ticket; <see cref="TicketPullStatus.NoPull"/> when it has none or the git host cannot say (the plain notice is then enough).</summary>
    private static async Task<TicketPullStatus> ReadTicketPullStatusAsync(string owner, string repo, int ticket)
    {
        try
        {
            return await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.ReadTicketPullStatusAsync(new TicketReference(owner, repo, ticket)),
                ActivityPolicy.Local(TimeSpan.FromSeconds(60), 3));
        }
        catch (ActivityFailureException)
        {
            return TicketPullStatus.NoPull;
        }
    }

    /// <summary>
    /// A comment with no command on a pull request. On one of the factory's own open pull requests it is probably a request for a change, which the factory does not read as one, so it is told once
    /// (per pull request) how to ask. Anywhere else, and when the git host cannot say, nothing is posted: people talk to each other on pull requests.
    /// </summary>
    private static async Task HintPullRequestAsync(PullHintRequest hint)
    {
        try
        {
            var target = await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.IsHintTargetAsync(hint),
                ActivityPolicy.Local(TimeSpan.FromSeconds(60), 3));
            if (!target)
            {
                return;
            }
            await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.PostNoticeUniqueAsync(new CommentRequest(hint.Owner, hint.Repo, hint.PullNumber, TicketComments.PullRequestCommentsAreNotRead)),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
        }
        catch (ActivityFailureException)
        {
            // Best effort: a hint that cannot be given costs nothing.
        }
    }

    private static async Task CloseTicketAsync(WorkRequest feature)
    {
        // A QA run on this ticket may be waiting for a go-ahead; closing the ticket ends that wait (nothing happens when none is running).
        await SignalAsync<QaWorkflow>(WorkIds.For(feature.Owner, feature.Repo, feature.IssueNumber, WorkIds.Qa), workflow => workflow.OnTicketClosedAsync());
        if (feature.IsIncident)
        {
            await SignalAsync<IncidentWorkflow>(feature.WorkflowId, workflow => workflow.OnTicketClosedAsync());
        }
        else if (feature.IsPlan)
        {
            await SignalAsync<PlanWorkflow>(feature.WorkflowId, workflow => workflow.OnTicketClosedAsync());
        }
        else if (feature.IsDocs)
        {
            await SignalAsync<DocsWorkflow>(feature.WorkflowId, workflow => workflow.OnTicketClosedAsync());
        }
        else if (feature.IsBug)
        {
            await SignalAsync<BugWorkflow>(feature.WorkflowId, workflow => workflow.OnTicketClosedAsync());
        }
        else
        {
            await SignalAsync<FeatureWorkflow>(feature.WorkflowId, workflow => workflow.OnTicketClosedAsync());
        }
    }

    /// <summary>Sends the signal; false when there is no running workflow for this ticket (never started, or finished).</summary>
    private static async Task<bool> SignalAsync<TWorkflow>(
        string workflowId, System.Linq.Expressions.Expression<Func<TWorkflow, Task>> signal)
    {
        try
        {
            await Workflow.GetExternalWorkflowHandle<TWorkflow>(workflowId).SignalAsync(signal);
            return true;
        }
        catch (FailureException)
        {
            return false;
        }
    }
}
