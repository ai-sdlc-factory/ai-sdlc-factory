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
            RoutingAction.StartFeature => StartTicketWorkflowAsync(decision),
            RoutingAction.RevisePullRequest => RevisePullRequestAsync(decision.Revision!),
            RoutingAction.RetryFeature => RetryTicketAsync(decision),
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
            else if (feature.IsBug)
            {
                await Workflow.StartChildWorkflowAsync((BugWorkflow workflow) => workflow.RunAsync(feature), options);
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

    /// <summary>`/retry`: starts the ticket's run again when the last one has finished; while one is running, says so once.</summary>
    private static async Task RetryTicketAsync(RoutingDecision decision)
    {
        if (await StartTicketWorkflowAsync(decision))
        {
            return;
        }
        var feature = decision.Feature!;
        await Workflow.ExecuteActivityAsync(
            (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
                new CommentRequest(feature.Owner, feature.Repo, feature.IssueNumber, TicketComments.RetryWhileRunning)),
            ActivityPolicy.Local(TimeSpan.FromSeconds(30), 3));
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
        if (feature.IsIncident)
        {
            await SignalAsync<IncidentWorkflow>(feature.WorkflowId, workflow => workflow.OnTicketClosedAsync());
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
