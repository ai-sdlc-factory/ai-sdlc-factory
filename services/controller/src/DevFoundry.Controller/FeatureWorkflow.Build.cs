using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public sealed partial class FeatureWorkflow
{
    private const int MaxBuildRounds = 3;
    private const int DeveloperTimeoutSeconds = 1800;

    private static (DeveloperReport? Value, string? Error) ParseDeveloper(string text) =>
        DeveloperParser.TryParse(text, out var report, out var error) ? (report, null) : (null, error);

    /// <summary>
    /// Build stage. Each round the developer works in the writable workspace, then fixed checks decide:
    /// protected paths, symlinks, size and secrets first, then the project's own checks. All pass → the
    /// factory (not the agent) commits, pushes and opens the pull request. Anything else goes back to the
    /// developer as feedback, up to a fixed number of rounds.
    /// </summary>
    private async Task<FeatureResult> BuildAsync(
        FeatureRequest request, WorkspaceInfo workspace, ProjectConfig project,
        IReadOnlyList<CriterionCoverage> remaining, ArchitectDesign? design, TriageDecision triage)
    {
        string? feedback = null;
        DeveloperReport? report = null;
        IReadOnlyList<GateResult> passed = [];
        var passedRound = 0;

        for (var round = 1; round <= MaxBuildRounds && report is null; round++)
        {
            if (ticketClosed)
            {
                return new FeatureResult("closed");
            }

            var feedbackForRound = feedback;
            var outcome = await RunAgentAsync<DeveloperReport>(
                "developer", project.Image, workspace,
                problem => DeveloperPrompt.Build(request, remaining, design, project.Gates, Combine(feedbackForRound, problem), round, MaxBuildRounds),
                ParseDeveloper, writable: true, timeoutSeconds: DeveloperTimeoutSeconds);
            if (ticketClosed)
            {
                return new FeatureResult("closed");
            }
            if (outcome.NotSignedIn)
            {
                await CommentAsync(request, NotSignedInMessage);
                return new FeatureResult("blocked");
            }
            if (outcome.Value is not { } developer)
            {
                await CommentAsync(request, $"The build failed and needs a person to look at it: {TicketComments.Inert(Short(outcome.Problem ?? "unknown problem"))}");
                return new FeatureResult("build_failed");
            }
            if (developer.Verdict == DeveloperReport.Blocked)
            {
                await CommentAsync(request, "**Build: the developer is blocked**\n\n" + string.Join('\n', developer.Notes.Select(note => $"- {TicketComments.Inert(note)}")));
                return new FeatureResult("build_blocked");
            }

            var before = await DetectChangesAsync(workspace);
            if (before.Violations.Count > 0 || before.Files.Count == 0)
            {
                feedback = before.Files.Count == 0 && before.Violations.Count == 0
                    ? "You reported the work as done, but no files in the workspace changed."
                    : BuildFeedback.FromViolations(before.Violations);
                await CommentAsync(request, $"Build round {round}: the changes were rejected: {TicketComments.Inert(Short(feedback))}");
                continue;
            }

            var results = new List<GateResult>();
            foreach (var gate in project.Gates)
            {
                results.Add(await RunGateAsync(project.Image, workspace, gate));
            }

            // Running the checks can itself create files (build output, caches); look again before trusting the tree.
            var after = await DetectChangesAsync(workspace);
            feedback = BuildFeedback.FromGates(results);
            if (feedback is null && after.Violations.Count > 0)
            {
                feedback = BuildFeedback.FromViolations(after.Violations);
            }
            if (feedback is not null)
            {
                await CommentAsync(request, $"Build round {round}: {BuildFeedback.Summary(results)}." +
                    (after.Violations.Count > 0 ? $" Also rejected: {TicketComments.Inert(Short(string.Join("; ", after.Violations.Take(3))))}" : ""));
                continue;
            }

            report = developer;
            passed = results;
            passedRound = round;
            await CommentAsync(request, $"Build round {round}: {BuildFeedback.Summary(results)}. Opening a pull request.");
        }

        if (report is null)
        {
            await CommentAsync(request,
                $"I could not get the work to pass the checks in {MaxBuildRounds} rounds, so nothing was published. " +
                $"Last problem: {TicketComments.Inert(Short(feedback ?? "unknown"))} A person needs to take over.");
            return new FeatureResult("build_failed", triage.NeedsArchitect);
        }

        var title = PullRequestText.Title(request);
        PublishResult? published;
        PullRequestInfo pullRequest;
        try
        {
            published = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.PublishAsync(new PublishRequest(
                    workspace.Name, $"{title} (#{request.IssueNumber})\n\nBuilt by the factory for {request.Owner}/{request.Repo}#{request.IssueNumber}.\n\n{TicketComments.Inert(report.Summary)}")),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(5), RetryPolicy = new RetryPolicy { MaximumAttempts = 3 } });
            if (published is null)
            {
                await CommentAsync(request, "The work produced no changes to publish.");
                return new FeatureResult("no_changes", triage.NeedsArchitect);
            }
            pullRequest = await Workflow.ExecuteActivityAsync(
                (ForgejoActivities activities) => activities.CreatePullRequestAsync(new PullRequestRequest(
                    request.Owner, request.Repo, published.Branch, workspace.DefaultBranch, title,
                    PullRequestText.Body(request, report, remaining, design, passed, passedRound))),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(60), RetryPolicy = new RetryPolicy { MaximumAttempts = 3 } });
        }
        catch (ActivityFailureException error)
        {
            await CommentAsync(request, $"Publishing failed and needs a person to look at it: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))}");
            return new FeatureResult("publish_failed", triage.NeedsArchitect);
        }

        await CommentAsync(request, $"**Pull request opened:** {pullRequest.Url}\n\n{published.FileCount} file(s) on branch `{published.Branch}`. A person must review and merge it.");
        return new FeatureResult("pr_opened", triage.NeedsArchitect, pullRequest.Url);
    }

    private static string? Combine(string? feedback, string? unusableAnswer) =>
        unusableAnswer is null ? feedback
        : (feedback is null ? "" : feedback + "\n\n") + $"Your previous reply could not be used: {unusableAnswer}. Reply again with exactly one JSON object in the required shape.";

    private static Task<ChangeReport> DetectChangesAsync(WorkspaceInfo workspace) =>
        Workflow.ExecuteActivityAsync(
            (WorkspaceActivities activities) => activities.DetectChangesAsync(workspace.Name),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(3), RetryPolicy = new RetryPolicy { MaximumAttempts = 2 } });

    /// <summary>Runs one project check in the sandbox without model access. A check that cannot run counts as failed.</summary>
    private static async Task<GateResult> RunGateAsync(string image, WorkspaceInfo workspace, Gate gate)
    {
        try
        {
            var result = await Workflow.ExecuteActivityAsync<SandboxRunResult>(
                SandboxNames.ActivityName,
                [new SandboxRunRequest(image, workspace.Name, WorkspaceWritable: true, gate.Command, gate.TimeoutSeconds)],
                new ActivityOptions
                {
                    TaskQueue = SandboxNames.DefaultTaskQueue,
                    StartToCloseTimeout = TimeSpan.FromSeconds(gate.TimeoutSeconds + 300),
                    HeartbeatTimeout = TimeSpan.FromSeconds(30),
                    RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
                });
            var output = string.Join('\n', new[] { result.Stdout, result.Stderr }.Where(part => part.Length > 0));
            return new GateResult(gate.Name, result.ExitCode == 0 && !result.TimedOut, result.ExitCode, result.TimedOut, output);
        }
        catch (ActivityFailureException error)
        {
            return new GateResult(gate.Name, false, -1, false, $"the check could not run: {error.InnerException?.Message ?? error.Message}");
        }
    }
}
