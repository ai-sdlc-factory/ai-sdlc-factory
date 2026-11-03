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
                    // Abandon: the feature workflow outlives this short routing run.
                    await Workflow.StartChildWorkflowAsync(
                        (FeatureWorkflow workflow) => workflow.RunAsync(decision.Feature!),
                        new ChildWorkflowOptions
                        {
                            Id = decision.Feature!.WorkflowId,
                            ParentClosePolicy = ParentClosePolicy.Abandon,
                            IdReusePolicy = decision.Restart ? WorkflowIdReusePolicy.AllowDuplicate : WorkflowIdReusePolicy.RejectDuplicate,
                        });
                }
                catch (WorkflowAlreadyStartedException)
                {
                    // Relabeling or editing a ticket that already has a workflow is not an error.
                }
                break;

            case RoutingAction.ReplyToFeature:
                if (!await SignalAsync(decision.Feature!.WorkflowId, workflow => workflow.OnHumanReplyAsync(decision.Reply!)))
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
                await SignalAsync(decision.Feature!.WorkflowId, workflow => workflow.OnTicketClosedAsync());
                break;
        }
    }

    /// <summary>Sends the signal; false when there is no running workflow for this ticket (never started, or finished).</summary>
    private static async Task<bool> SignalAsync(
        string workflowId, System.Linq.Expressions.Expression<Func<FeatureWorkflow, Task>> signal)
    {
        try
        {
            await Workflow.GetExternalWorkflowHandle<FeatureWorkflow>(workflowId).SignalAsync(signal);
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
