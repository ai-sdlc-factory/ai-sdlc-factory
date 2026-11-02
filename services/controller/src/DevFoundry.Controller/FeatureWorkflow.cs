using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public sealed record FeatureResult(string Status, bool? NeedsArchitect = null);

/// <summary>
/// One run per feature ticket, following docs/feature-factory.md. Built so far: intake (a private
/// workspace), the analyst, the coverage check and triage. The workflow code owns every decision; agents only return a verdict
/// value, which is validated before it is used.
/// </summary>
[Workflow("FeatureWorkflow")]
public sealed class FeatureWorkflow
{
    private static readonly TimeSpan WaitForHuman = TimeSpan.FromDays(7);
    private const int MaxClarificationRounds = 4;
    private const int MaxAgentAttempts = 2;

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

            return await StagesAsync(request, workspace);
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
    /// The stages after intake, in order: analyst, coverage check, triage. Each either stops the
    /// workflow with a status or hands its result to the next one.
    /// </summary>
    private async Task<FeatureResult> StagesAsync(FeatureRequest request, WorkspaceInfo workspace)
    {
        var analysis = await AnalyseAsync(request, workspace);
        if (analysis.Stop is { } stop)
        {
            return stop;
        }
        var verdict = analysis.Verdict!;

        var coverage = await CheckCoverageAsync(request, workspace, verdict);
        if (coverage.Stop is { } stopped)
        {
            return stopped;
        }

        var triage = Triage.Decide(verdict.Complexity, request.Labels ?? []);
        await CommentAsync(request, TicketComments.TriageDecision(triage));
        await CommentAsync(request, "The next stages (design, build, review, QA) are not built yet, so I stop here.");
        return new FeatureResult("triaged", triage.NeedsArchitect);
    }

    private sealed record StageResult<T>(FeatureResult? Stop, T? Verdict) where T : class;

    /// <summary>
    /// Analyst stage. pass → continue; reject → stop; needs_clarification → ask on the ticket, wait for
    /// a human, and run again with their answers (a bounded number of rounds).
    /// </summary>
    private async Task<StageResult<AnalystVerdict>> AnalyseAsync(FeatureRequest request, WorkspaceInfo workspace)
    {
        var answers = new List<string>();

        for (var round = 1; round <= MaxClarificationRounds; round++)
        {
            // Answers that arrived while the analyst was running count for this round.
            DrainReplies(answers);
            if (ticketClosed)
            {
                return Stop<AnalystVerdict>("closed");
            }

            var outcome = await RunAgentAsync<AnalystVerdict>(
                "analyst", workspace,
                problem => AnalystPrompt.Build(request, answers, problem, round, MaxClarificationRounds),
                ParseAnalyst);
            if (ticketClosed)
            {
                return Stop<AnalystVerdict>("closed");
            }
            if (outcome.NotSignedIn)
            {
                await CommentAsync(request, NotSignedInMessage);
                return Stop<AnalystVerdict>("blocked");
            }
            if (outcome.Value is not { } verdict)
            {
                await CommentAsync(request, $"The analysis failed and needs a person to look at it: {TicketComments.Inert(Short(outcome.Problem ?? "unknown problem"))}");
                return Stop<AnalystVerdict>("analysis_failed");
            }

            await CommentAsync(request, TicketComments.Analysis(verdict));
            switch (verdict.Verdict)
            {
                case AnalystVerdict.Pass:
                    return new StageResult<AnalystVerdict>(null, verdict);

                case AnalystVerdict.Reject:
                    return Stop<AnalystVerdict>("rejected");

                default: // needs_clarification
                    var answered = await Workflow.WaitConditionAsync(() => ticketClosed || replies.Count > 0, WaitForHuman);
                    if (ticketClosed)
                    {
                        return Stop<AnalystVerdict>("closed");
                    }
                    if (!answered)
                    {
                        await CommentAsync(request, "No answer for 7 days; stopping. Re-label the ticket to start again.");
                        return Stop<AnalystVerdict>("timed_out");
                    }
                    break;
            }
        }

        await CommentAsync(request, $"Still unclear after {MaxClarificationRounds} rounds of questions. A person needs to decide how to proceed.");
        return Stop<AnalystVerdict>("needs_human");
    }

    /// <summary>
    /// Coverage stage. Fully covered → stop (nothing to build); otherwise continue. Whatever the agent
    /// claims is checked against the workspace first, and a failed check can only weaken a claim.
    /// </summary>
    private async Task<StageResult<CoverageReport>> CheckCoverageAsync(
        FeatureRequest request, WorkspaceInfo workspace, AnalystVerdict analysis)
    {
        var outcome = await RunAgentAsync<CoverageReport>(
            "coverage", workspace,
            problem => CoveragePrompt.Build(request, analysis.AcceptanceCriteria, problem),
            text => ParseCoverage(text, analysis.AcceptanceCriteria));
        if (ticketClosed)
        {
            return Stop<CoverageReport>("closed");
        }
        if (outcome.NotSignedIn)
        {
            await CommentAsync(request, NotSignedInMessage);
            return Stop<CoverageReport>("blocked");
        }
        if (outcome.Value is not { } claimed)
        {
            await CommentAsync(request, $"The coverage check failed and needs a person to look at it: {TicketComments.Inert(Short(outcome.Problem ?? "unknown problem"))}");
            return Stop<CoverageReport>("coverage_failed");
        }

        var report = EvidenceCheck.Apply(claimed, await FindExistingEvidenceAsync(workspace, claimed));
        await CommentAsync(request, TicketComments.Coverage(report));
        if (report.Verdict == CoverageVerdicts.FullyCovered)
        {
            await CommentAsync(request, "Every criterion is already met, so there is nothing to build. If that is wrong, say what is missing on this ticket and re-label it.");
            return Stop<CoverageReport>("already_implemented");
        }
        return new StageResult<CoverageReport>(null, report);
    }

    /// <summary>Which of the cited files exist. If the check cannot run, nothing counts as verified.</summary>
    private static async Task<HashSet<string>> FindExistingEvidenceAsync(WorkspaceInfo workspace, CoverageReport claimed)
    {
        var paths = EvidenceCheck.PathsToCheck(claimed);
        if (paths.Count == 0)
        {
            return [];
        }
        try
        {
            var result = await Workflow.ExecuteActivityAsync<SandboxRunResult>(
                SandboxNames.ActivityName,
                [new SandboxRunRequest("dotnet", workspace.Name, false, EvidenceCheck.Command(paths), 60)],
                new ActivityOptions
                {
                    TaskQueue = SandboxNames.DefaultTaskQueue,
                    StartToCloseTimeout = TimeSpan.FromMinutes(3),
                    HeartbeatTimeout = TimeSpan.FromSeconds(30),
                    RetryPolicy = new RetryPolicy { MaximumAttempts = 2 },
                });
            return result.ExitCode == 0 ? EvidenceCheck.ParseFound(result.Stdout) : [];
        }
        catch (ActivityFailureException)
        {
            return [];
        }
    }

    private static (AnalystVerdict? Value, string? Error) ParseAnalyst(string text) =>
        VerdictParser.TryParseAnalyst(text, out var verdict, out var error) ? (verdict, null) : (null, error);

    private static (CoverageReport? Value, string? Error) ParseCoverage(string text, IReadOnlyList<string> criteria) =>
        CoverageParser.TryParse(text, criteria, out var report, out var error) ? (report, null) : (null, error);

    private const string NotSignedInMessage =
        "OpenCode is not signed in, so I cannot run the agents. Run the `opencode-login` service, then re-label this ticket.";

    private static StageResult<T> Stop<T>(string status) where T : class => new(new FeatureResult(status), null);

    private sealed record AgentOutcome<T>(T? Value, bool NotSignedIn, string? Problem) where T : class;

    /// <summary>
    /// Runs one agent role in a read-only sandbox with model access and parses its answer. An unusable
    /// answer gets one more try that says what was wrong; after that the caller asks a person.
    /// </summary>
    private static async Task<AgentOutcome<T>> RunAgentAsync<T>(
        string role, WorkspaceInfo workspace, Func<string?, string> prompt, Func<string, (T? Value, string? Error)> parse)
        where T : class
    {
        string? problem = null;
        for (var attempt = 1; attempt <= MaxAgentAttempts; attempt++)
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
                        Command: ["opencode", "run", "--agent", role, "--format", "json", prompt(problem)],
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
                return message.Contains("not signed in", StringComparison.OrdinalIgnoreCase)
                    ? new AgentOutcome<T>(null, true, null)
                    : new AgentOutcome<T>(null, false, message);
            }

            if (result.ExitCode == 78)
            {
                return new AgentOutcome<T>(null, true, null);
            }
            if (result.TimedOut)
            {
                problem = $"the {role} ran out of time";
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
            var (value, error2) = parse(text);
            if (value is not null)
            {
                return new AgentOutcome<T>(value, false, null);
            }
            problem = error2;
        }
        return new AgentOutcome<T>(null, false, problem);
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
