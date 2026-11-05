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

    private static async Task RouteAsync(ForgejoEvent input)
    {
        var decision = EventRouter.Decide(input);
        switch (decision.Action)
        {
            case RoutingAction.StartFeature:
                try
                {
                    // Abandon: the ticket's workflow outlives this short routing run.
                    var options = new ChildWorkflowOptions
                    {
                        Id = decision.Feature!.WorkflowId,
                        ParentClosePolicy = ParentClosePolicy.Abandon,
                        IdReusePolicy = decision.Restart ? WorkflowIdReusePolicy.AllowDuplicate : WorkflowIdReusePolicy.RejectDuplicate,
                    };
                    if (decision.Feature.IsIncident)
                    {
                        await Workflow.StartChildWorkflowAsync((IncidentWorkflow workflow) => workflow.RunAsync(decision.Feature!), options);
                    }
                    else if (decision.Feature.IsBug)
                    {
                        await Workflow.StartChildWorkflowAsync((BugWorkflow workflow) => workflow.RunAsync(decision.Feature!), options);
                    }
                    else
                    {
                        await Workflow.StartChildWorkflowAsync((FeatureWorkflow workflow) => workflow.RunAsync(decision.Feature!), options);
                    }
                }
                catch (WorkflowAlreadyStartedException)
                {
                    // Relabeling or editing a ticket that already has a workflow is not an error.
                }
                break;

            case RoutingAction.RevisePullRequest:
                var revision = decision.Revision!;
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
                    var busy = new CommentRequest(revision.Owner, revision.Repo, revision.PullNumber, TicketComments.RevisionBusy);
                    await Workflow.ExecuteActivityAsync(
                        (ForgejoActivities activities) => activities.PostNoticeOnceAsync(busy),
                        new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30), RetryPolicy = new RetryPolicy { MaximumAttempts = 3 } });
                }
                break;

            case RoutingAction.ReplyToFeature:
                var delivered = decision.Feature!.IsIncident
                    ? await SignalAsync<IncidentWorkflow>(decision.Feature.WorkflowId, workflow => workflow.OnHumanReplyAsync(decision.Reply!))
                    : decision.Feature.IsBug
                        ? await SignalAsync<BugWorkflow>(decision.Feature.WorkflowId, workflow => workflow.OnHumanReplyAsync(decision.Reply!))
                        : await SignalAsync<FeatureWorkflow>(decision.Feature.WorkflowId, workflow => workflow.OnHumanReplyAsync(decision.Reply!));
                if (!delivered)
                {
                    // Nobody is listening: the run finished, or never started. Say so instead of staying silent.
                    var feature = decision.Feature!;
                    await Workflow.ExecuteActivityAsync(
                        (ForgejoActivities activities) => activities.PostNoticeOnceAsync(
                            new CommentRequest(feature.Owner, feature.Repo, feature.IssueNumber, TicketComments.NoRunActive)),
                        new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30), RetryPolicy = new RetryPolicy { MaximumAttempts = 3 } });
                }
                break;

            case RoutingAction.CloseFeature:
                if (decision.Feature!.IsIncident)
                {
                    await SignalAsync<IncidentWorkflow>(decision.Feature.WorkflowId, workflow => workflow.OnTicketClosedAsync());
                }
                else if (decision.Feature.IsBug)
                {
                    await SignalAsync<BugWorkflow>(decision.Feature.WorkflowId, workflow => workflow.OnTicketClosedAsync());
                }
                else
                {
                    await SignalAsync<FeatureWorkflow>(decision.Feature.WorkflowId, workflow => workflow.OnTicketClosedAsync());
                }
                break;
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
