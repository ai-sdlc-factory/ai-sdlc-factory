using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public sealed record FeatureResult(string Status);

/// <summary>
/// One run per feature ticket. This is the skeleton the stages from docs/feature-factory.md will
/// be added to. For now it prepares a workspace, checks that a sandbox can see it, comments, waits
/// for a human reply or for the ticket to be closed, and cleans up.
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
        await CommentAsync(request, "The factory picked up this feature ticket. Preparing a workspace.");

        try
        {
            // Intake: a private checkout on a working branch, then a smoke check that a sandbox can
            // see it. Later stages (analyst, developer, ...) run in sandboxes the same way.
            WorkspaceInfo workspace;
            SandboxRunResult check;
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
                check = await Workflow.ExecuteActivityAsync<SandboxRunResult>(
                    SandboxNames.ActivityName,
                    [new SandboxRunRequest(
                        Image: "dotnet",
                        Workspace: workspace.Name,
                        WorkspaceWritable: false,
                        Command: ["git", "log", "-1", "--format=%h %s"],
                        TimeoutSeconds: 120)],
                    new ActivityOptions
                    {
                        TaskQueue = SandboxNames.DefaultTaskQueue,
                        StartToCloseTimeout = TimeSpan.FromMinutes(5),
                        HeartbeatTimeout = TimeSpan.FromSeconds(30),
                        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
                    });
            }
            catch (ActivityFailureException error)
            {
                await CommentAsync(request, $"Setting up the workspace failed: {Short(error.InnerException?.Message ?? error.Message)}");
                return new FeatureResult("setup_failed");
            }

            var agentCheck = await CheckAgentsAsync(workspace);
            await CommentAsync(request,
                $"Workspace ready: branch `{workspace.Branch}` from `{workspace.DefaultBranch}` at `{workspace.HeadCommit[..7]}`. " +
                $"Sandbox check {(check.ExitCode == 0 ? "passed" : $"failed (exit {check.ExitCode})")}: `{Short(check.Stdout.Trim())}`. " +
                $"{agentCheck} " +
                "The analysis stage is not built yet, so I only wait for a reply here to confirm the round trip works.");

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

    private static readonly string[] BuiltInAgents = ["build", "compaction", "explore", "general", "plan", "summary", "title"];

    /// <summary>
    /// Reports whether OpenCode is signed in and which factory agents it can see. A missing login is
    /// information for the ticket, not a failure: the workspace itself is fine.
    /// </summary>
    private static async Task<string> CheckAgentsAsync(WorkspaceInfo workspace)
    {
        try
        {
            var result = await Workflow.ExecuteActivityAsync<SandboxRunResult>(
                SandboxNames.ActivityName,
                [new SandboxRunRequest(
                    Image: "dotnet",
                    Workspace: workspace.Name,
                    WorkspaceWritable: false,
                    Command: ["sh", "-c", "opencode auth list 2>&1; opencode agent list 2>&1"],
                    TimeoutSeconds: 120,
                    NeedsModelAuth: true)],
                new ActivityOptions
                {
                    TaskQueue = SandboxNames.DefaultTaskQueue,
                    StartToCloseTimeout = TimeSpan.FromMinutes(5),
                    HeartbeatTimeout = TimeSpan.FromSeconds(30),
                    RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
                });

            if (result.ExitCode == 78)
            {
                return "OpenCode is not signed in yet (run the `opencode-login` service).";
            }

            var output = System.Text.RegularExpressions.Regex.Replace(result.Stdout, "\x1b\\[[0-9;]*m", "");
            var credentials = System.Text.RegularExpressions.Regex.Match(output, @"(\d+) credentials?");
            var agents = System.Text.RegularExpressions.Regex.Matches(output, @"^([a-z][a-z0-9_-]*) \((?:primary|subagent)\)", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(match => match.Groups[1].Value)
                .Where(name => !BuiltInAgents.Contains(name))
                .ToList();
            return $"OpenCode login: {(credentials.Success ? credentials.Groups[1].Value : "?")} credential(s). " +
                   $"Factory agents: {(agents.Count == 0 ? "none found" : string.Join(", ", agents))}.";
        }
        catch (ActivityFailureException error)
        {
            return $"OpenCode check failed: {Short(error.InnerException?.Message ?? error.Message)}";
        }
    }

    /// <summary>Keeps repository-supplied text short and free of characters that break the comment's formatting.</summary>
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
