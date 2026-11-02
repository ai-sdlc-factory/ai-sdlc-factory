using Temporalio.Common;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public sealed record FeatureResult(string Status);

/// <summary>
/// One run per feature ticket. This is the skeleton the stages from docs/feature-factory.md will
/// be added to; for now it proves the ticket round trip: it comments, waits for a human reply or
/// for the ticket to be closed, and finishes.
/// </summary>
[Workflow("FeatureWorkflow")]
public sealed class FeatureWorkflow
{
    private static readonly TimeSpan WaitForHuman = TimeSpan.FromDays(7);

    private readonly List<HumanReply> replies = [];
    private bool ticketClosed;

    [WorkflowSignal("human_reply")]
    public Task OnHumanReplyAsync(HumanReply reply)
    {
        replies.Add(reply);
        return Task.CompletedTask;
    }

    [WorkflowSignal("ticket_closed")]
    public Task OnTicketClosedAsync()
    {
        ticketClosed = true;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<FeatureResult> RunAsync(FeatureRequest request)
    {
        await CommentAsync(request,
            "The factory picked up this feature ticket. The analysis stage is not built yet, so for now " +
            "I only wait for a reply here to confirm the round trip works.");

        var signalled = await Workflow.WaitConditionAsync(
            () => ticketClosed || replies.Count > 0, WaitForHuman);
        if (ticketClosed)
        {
            return new FeatureResult("closed");
        }
        if (!signalled)
        {
            await CommentAsync(request, "No reply for 7 days; stopping. Re-label the ticket to start again.");
            return new FeatureResult("timed_out");
        }

        await CommentAsync(request, $"Reply from @{replies[0].Author} received. Stopping here for now.");
        return new FeatureResult("replied");
    }

    private static Task CommentAsync(FeatureRequest request, string body) =>
        Workflow.ExecuteActivityAsync(
            (ForgejoActivities activities) => activities.PostIssueCommentAsync(
                new CommentRequest(request.Owner, request.Repo, request.IssueNumber, body)),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromSeconds(30),
                RetryPolicy = new RetryPolicy { MaximumAttempts = 5 },
            });
}
