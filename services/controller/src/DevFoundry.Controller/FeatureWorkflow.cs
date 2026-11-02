using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public sealed record FeatureResult(string Status);

/// <summary>
/// One run per feature ticket, following docs/feature-factory.md. Built so far: intake (a private
/// workspace) and the analyst. The workflow code owns every decision; agents only return a verdict
/// value, which is validated before it is used.
/// </summary>
[Workflow("FeatureWorkflow")]
public sealed class FeatureWorkflow
{
    private static readonly TimeSpan WaitForHuman = TimeSpan.FromDays(7);
    private const int MaxClarificationRounds = 4;
    private const int MaxAnalystAttempts = 2;

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
        await CommentAsync(request, "The factory picked up this feature ticket. Preparing a workspace.");

        try
        {
            WorkspaceInfo workspace;
            try
            {
                workspace = await Workflow.ExecuteActivityAsync(
                    (WorkspaceActivities activities) => activities.PrepareAsync(
                        new WorkspaceRequest(request.Owner, request.Repo, request.WorkflowId, $"factory/issue-{request.IssueNumber}")),
                    new ActivityOptions
                    {
                        StartToCloseTimeout = TimeSpan.FromMinutes(5),
                        RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
                    });
            }
            catch (ActivityFailureException error)
            {
                await CommentAsync(request, $"Setting up the workspace failed: {Short(error.InnerException?.Message ?? error.Message)}");
                return new FeatureResult("setup_failed");
            }

            return await AnalyseAsync(request, workspace);
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
    /// Analyst stage. pass → done for now; reject → stop; needs_clarification → ask on the ticket,
    /// wait for a human, and run again with their answers (a bounded number of rounds).
    /// </summary>
    private async Task<FeatureResult> AnalyseAsync(FeatureRequest request, WorkspaceInfo workspace)
    {
        var answers = new List<string>();

        for (var round = 1; round <= MaxClarificationRounds; round++)
        {
            // Answers that arrived while the analyst was running count for this round.
            DrainReplies(answers);
            if (ticketClosed)
            {
                return new FeatureResult("closed");
            }

            var outcome = await RunAnalystAsync(request, workspace, answers, round);
            if (ticketClosed)
            {
                return new FeatureResult("closed");
            }
            if (outcome.NotSignedIn)
            {
                await CommentAsync(request, "OpenCode is not signed in, so I cannot run the analyst. Run the `opencode-login` service, then re-label this ticket.");
                return new FeatureResult("blocked");
            }
            if (outcome.Verdict is not { } verdict)
            {
                await CommentAsync(request, $"The analysis failed and needs a person to look at it: {TicketComments.Inert(Short(outcome.Problem ?? "unknown problem"))}");
                return new FeatureResult("analysis_failed");
            }

            await CommentAsync(request, TicketComments.Analysis(verdict));
            switch (verdict.Verdict)
            {
                case AnalystVerdict.Pass:
                    await CommentAsync(request, "The next stages (design, build, review, QA) are not built yet, so I stop here.");
                    return new FeatureResult("analysed");

                case AnalystVerdict.Reject:
                    return new FeatureResult("rejected");

                default: // needs_clarification
                    var answered = await Workflow.WaitConditionAsync(() => ticketClosed || replies.Count > 0, WaitForHuman);
                    if (ticketClosed)
                    {
                        return new FeatureResult("closed");
                    }
                    if (!answered)
                    {
                        await CommentAsync(request, "No answer for 7 days; stopping. Re-label the ticket to start again.");
                        return new FeatureResult("timed_out");
                    }
                    break;
            }
        }

        await CommentAsync(request, $"Still unclear after {MaxClarificationRounds} rounds of questions. A person needs to decide how to proceed.");
        return new FeatureResult("needs_human");
    }

    private sealed record AnalystOutcome(AnalystVerdict? Verdict, bool NotSignedIn, string? Problem);

    /// <summary>Runs the analyst, retrying once with the reason when its answer is unusable.</summary>
    private static async Task<AnalystOutcome> RunAnalystAsync(
        FeatureRequest request, WorkspaceInfo workspace, IReadOnlyList<string> answers, int round)
    {
        string? problem = null;
        for (var attempt = 1; attempt <= MaxAnalystAttempts; attempt++)
        {
            SandboxRunResult result;
            try
            {
                result = await Workflow.ExecuteActivityAsync<SandboxRunResult>(
                    SandboxNames.ActivityName,
                    [new SandboxRunRequest(
                        Image: "dotnet",
                        Workspace: workspace.Name,
                        WorkspaceWritable: false,
                        Command: ["opencode", "run", "--agent", "analyst", "--format", "json", AnalystPrompt.Build(request, answers, problem, round, MaxClarificationRounds)],
                        TimeoutSeconds: 900,
                        NeedsModelAuth: true)],
                    new ActivityOptions
                    {
                        TaskQueue = SandboxNames.DefaultTaskQueue,
                        StartToCloseTimeout = TimeSpan.FromMinutes(20),
                        HeartbeatTimeout = TimeSpan.FromSeconds(30),
                        // A model run can consume plan usage; the retry below is a deliberate, bounded second try.
                        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
                    });
            }
            catch (ActivityFailureException error)
            {
                var message = error.InnerException?.Message ?? error.Message;
                if (message.Contains("not signed in", StringComparison.OrdinalIgnoreCase))
                {
                    return new AnalystOutcome(null, true, null);
                }
                return new AnalystOutcome(null, false, message);
            }

            if (result.ExitCode == 78)
            {
                return new AnalystOutcome(null, true, null);
            }
            if (result.TimedOut)
            {
                problem = "the analyst ran out of time";
                continue;
            }
            if (result.ExitCode != 0)
            {
                problem = $"OpenCode exited with code {result.ExitCode}: {Short(result.Stderr)}";
                continue;
            }

            var text = OpenCodeEvents.FinalText(result.Stdout);
            if (text is null)
            {
                problem = result.OutputTruncated ? "the output was too long to contain the answer" : "OpenCode returned no answer";
                continue;
            }
            if (VerdictParser.TryParseAnalyst(text, out var verdict, out var parseError))
            {
                return new AnalystOutcome(verdict, false, null);
            }
            problem = parseError;
        }
        return new AnalystOutcome(null, false, problem);
    }

    private void DrainReplies(List<string> answers)
    {
        foreach (var reply in replies)
        {
            answers.Add(reply.Body);
        }
        replies.Clear();
    }

    /// <summary>Keeps repository- or model-supplied text short and free of characters that break the comment's formatting.</summary>
    private static string Short(string text)
    {
        var flat = text.Replace('`', '\'').Replace('\n', ' ').Trim();
        return flat.Length > 200 ? flat[..200] + "..." : flat;
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
