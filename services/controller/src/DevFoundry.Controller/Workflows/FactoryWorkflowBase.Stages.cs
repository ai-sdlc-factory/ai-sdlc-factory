using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public abstract partial class FactoryWorkflowBase
{
    protected sealed record StageResult<T>(FeatureResult? Stop, T? Verdict) where T : class;

    /// <summary>
    /// Analyst stage. pass → continue; reject → stop; needs_clarification → ask on the ticket, wait for
    /// a human, and run again with their answers (a bounded number of rounds).
    /// </summary>
    protected Task<StageResult<AnalystVerdict>> AnalyseAsync(FeatureRequest request, WorkspaceInfo workspace, string image) =>
        ConverseAsync(
            request, workspace, image, "analyst", maxClarificationRounds + 1,
            (round, answers, problem) => AnalystPrompt.Build(request, answers, problem, round, maxClarificationRounds + 1),
            ParseAnalyst,
            TicketComments.Analysis,
            verdict => verdict.Verdict switch
            {
                AnalystVerdict.Pass => Next.Done,
                AnalystVerdict.Reject => Next.Stop,
                _ => Next.Ask,
            },
            failedStatus: "analysis_failed", rejectedStatus: "rejected", failureText: "The analysis failed");

    /// <summary>
    /// Architect stage, only when triage asked for it. Same shape as the analyst: pass → continue,
    /// reject → stop, needs_decision → ask on the ticket and run again with the answers.
    /// </summary>
    protected Task<StageResult<ArchitectDesign>> DesignAsync(
        FeatureRequest request, WorkspaceInfo workspace, string image, CoverageReport coverage, TriageDecision triage) =>
        ConverseAsync(
            request, workspace, image, "architect", Rounds(MaxDesignRounds),
            (round, answers, problem) => ArchitectPrompt.Build(
                request, coverage.Remaining.ToList(), triage.Reasons, answers, problem, round, Rounds(MaxDesignRounds)),
            ParseDesign,
            TicketComments.Design,
            design => design.Verdict switch
            {
                ArchitectDesign.Pass => Next.Done,
                ArchitectDesign.Reject => Next.Stop,
                _ => Next.Ask,
            },
            failedStatus: "design_failed", rejectedStatus: "design_rejected", failureText: "The design failed");

    protected enum Next { Done, Stop, Ask }

    /// <summary>
    /// One agent role that may need to talk to a person: run it, post its answer, and either continue,
    /// stop, or ask and wait for a reply on the ticket and run it again. The agent supplies the verdict
    /// value; <paramref name="next"/> (code) maps it to what happens. Rounds are bounded.
    /// </summary>
    protected async Task<StageResult<T>> ConverseAsync<T>(
        FeatureRequest request, WorkspaceInfo workspace, string image, string role, int maxRounds,
        Func<int, IReadOnlyList<string>, string?, string> prompt,
        Func<string, (T? Value, string? Error)> parse,
        Func<T, string> comment,
        Func<T, Next> next,
        string failedStatus, string rejectedStatus, string failureText, bool writable = false)
        where T : class
    {
        var answers = new List<string>();

        for (var round = 1; round <= maxRounds; round++)
        {
            // Answers that arrived while the agent was running count for this round.
            DrainReplies(answers);
            if (ticketClosed)
            {
                return Stop<T>("closed");
            }

            var outcome = await RunAgentAsync<T>(role, image, workspace, problem => prompt(round, answers, problem), parse, writable);
            if (ticketClosed)
            {
                return Stop<T>("closed");
            }
            if (outcome.NotSignedIn)
            {
                await CommentAsync(request, NotSignedInMessage);
                return Stop<T>("blocked");
            }
            if (outcome.Value is not { } value)
            {
                await CommentAsync(request, $"{failureText} and needs a person to look at it: {TicketComments.Inert(Short(outcome.Problem ?? "unknown problem"))}");
                return Stop<T>(failedStatus);
            }

            var commentText = comment(value);
            if (commentText.Length > 0)
            {
                await CommentAsync(request, commentText);
            }
            switch (next(value))
            {
                case Next.Done:
                    return new StageResult<T>(null, value);

                case Next.Stop:
                    return Stop<T>(rejectedStatus);

                default:
                    var answered = await Workflow.WaitConditionAsync(() => ticketClosed || replies.Count > 0, WaitForHuman);
                    if (ticketClosed)
                    {
                        return Stop<T>("closed");
                    }
                    if (!answered)
                    {
                        await CommentAsync(request, "No answer for 7 days; stopping. Close and reopen the ticket to start again.");
                        return Stop<T>("timed_out");
                    }
                    break;
            }
        }

        await CommentAsync(request, $"Still open after {maxRounds} rounds of questions. A person needs to decide how to proceed.");
        return Stop<T>("needs_human");
    }

    /// <summary>
    /// Coverage stage. Fully covered → stop (nothing to build); otherwise continue. Whatever the agent
    /// claims is checked against the workspace first, and a failed check can only weaken a claim.
    /// </summary>
    protected async Task<StageResult<CoverageReport>> CheckCoverageAsync(
        FeatureRequest request, WorkspaceInfo workspace, string image, AnalystVerdict analysis)
    {
        var outcome = await RunAgentAsync<CoverageReport>(
            "coverage", image, workspace,
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

        var report = EvidenceCheck.Apply(claimed, await FindExistingFilesAsync(workspace, image, EvidenceCheck.PathsToCheck(claimed)));
        await CommentAsync(request, TicketComments.Coverage(report));
        if (report.Verdict == CoverageVerdicts.FullyCovered)
        {
            await CommentAsync(request, "Every criterion is already met, so there is nothing to build. If that is wrong, say what is missing on this ticket, then close and reopen it.");
            return Stop<CoverageReport>("already_implemented");
        }
        return new StageResult<CoverageReport>(null, report);
    }

    /// <summary>Which of the cited files exist. If the check cannot run, nothing counts as verified.</summary>
    protected static async Task<HashSet<string>> FindExistingFilesAsync(WorkspaceInfo workspace, string image, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return [];
        }
        try
        {
            var result = await Workflow.ExecuteActivityAsync<SandboxRunResult>(
                SandboxNames.ActivityName,
                [new SandboxRunRequest(image, workspace.Name, false, EvidenceCheck.Command(paths), 60, Role: "evidence")],
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

    protected static (AnalystVerdict? Value, string? Error) ParseAnalyst(string text) =>
        VerdictParser.TryParseAnalyst(text, out var verdict, out var error) ? (verdict, null) : (null, error);

    protected static (CoverageReport? Value, string? Error) ParseCoverage(string text, IReadOnlyList<string> criteria) =>
        CoverageParser.TryParse(text, criteria, out var report, out var error) ? (report, null) : (null, error);

    protected static (ArchitectDesign? Value, string? Error) ParseDesign(string text) =>
        ArchitectParser.TryParse(text, out var design, out var error) ? (design, null) : (null, error);

    protected static StageResult<T> Stop<T>(string status) where T : class => new(new FeatureResult(status), null);
}
