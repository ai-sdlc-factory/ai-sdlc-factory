using System.Text.Json;
using Temporalio.Api.Enums.V1;
using ParentClosePolicy = Temporalio.Workflows.ParentClosePolicy;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

[Workflow("ForgejoEventWorkflow")]
public sealed class ForgejoEventWorkflow
{
    /// <summary>
    /// Preserves the input in workflow history, produces a small receipt, and routes feature
    /// tickets to their own long-running workflow. Webhook contents are data; this workflow
    /// executes no supplied commands.
    /// </summary>
    [WorkflowRun]
    public async Task<ForgejoReceipt> RunAsync(ForgejoEvent input)
    {
        await RouteAsync(input);

        var payload = input.Payload;
        string? repository = null;
        if (TryGet(payload, "repository", out var repo) && TryGet(repo, "full_name", out var name))
        {
            repository = AsString(name);
        }

        TryGet(payload, "action", out var action);
        TryGet(payload, "ref", out var reference);
        return new ForgejoReceipt(
            input.DeliveryId, input.Event, repository, AsString(action), AsString(reference));
    }

    private static Task RouteAsync(ForgejoEvent input)
    {
        var decision = EventRouter.Decide(input);
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
            (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
                new CommentRequest(feature.Owner, feature.Repo, feature.IssueNumber, TicketComments.ReopenStillStopping)),
            ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
    }

    /// <summary>About 30 minutes: longer than the longest step a closed run can be in the middle of.</summary>
    private const int MaxReopenWaits = 120;

    /// <summary>The factory's open pull request for a ticket, or null when it has none (or Forgejo cannot say).</summary>
    private static async Task<int?> FindOpenPullRequestAsync(string owner, string repo, int ticket)
    {
        try
        {
            return await Workflow.ExecuteActivityAsync(
                (ForgejoActivities activities) => activities.FindFactoryPullRequestAsync(new TicketReference(owner, repo, ticket)),
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
                (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
                    new CommentRequest(feature.Owner, feature.Repo, feature.IssueNumber, TicketComments.RetryHasPullRequest(pull))),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
            return;
        }
        if (await StartTicketWorkflowAsync(decision))
        {
            return;
        }
        await Workflow.ExecuteActivityAsync(
            (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
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
                (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
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
                (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
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
                (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
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
                (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
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
                (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
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
                (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
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
                (ForgejoActivities activities) => activities.PostNoticeOnceAsync(busy),
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
            // Nobody is listening: the run finished, or never started. Say so instead of staying silent.
            await Workflow.ExecuteActivityAsync(
                (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
                    new CommentRequest(feature.Owner, feature.Repo, feature.IssueNumber, TicketComments.NoRunActive)),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
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

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static string? AsString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
