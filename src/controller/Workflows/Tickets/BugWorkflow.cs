using Temporalio.Workflows;

namespace AiSdlcFactory.Controller;

/// <summary>
/// One run per bug ticket. The aim is a fix that is proven by a test that fails before and passes after:
/// bug analyst, reproducer (writes a failing test), the factory's own "red" check (the test must fail on the unchanged
/// code), diagnostician (root cause), then the shared build with the developer, the project's checks, the "green" check,
/// and the reviewers. The acceptance check is replaced by the red/green proof. Quick mode skips the diagnosis.
/// </summary>
[Workflow("BugWorkflow")]
public sealed partial class BugWorkflow : FactoryWorkflowBase
{
    private const int MaxReproductionAttempts = 3;
    private const int MaxReproductionRounds = 2;

    [WorkflowSignal("human_reply")]
    public Task OnHumanReplyAsync(HumanReply reply) => AcceptHumanReplyAsync(reply);

    [WorkflowSignal("ticket_closed")]
    public Task OnTicketClosedAsync()
    {
        MarkTicketClosed();
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public Task<WorkResult> RunAsync(WorkRequest request) => RunPipelineAsync(request, "bug", StagesAsync);

    private async Task<WorkResult> StagesAsync(WorkContext work)
    {
        var (request, workspace, project) = work;
        // What an earlier run finished (only on /retry, and only if the ticket and the default branch are as they were) is not done again. The reproduction
        // is not kept: its test files live in the checkout, which is removed when a run ends.
        var saved = await BeginResumeAsync(request, workspace);
        var understood = saved.BugAnalysis;
        if (understood is null)
        {
            var analysis = await ConverseAsync<BugAnalysis>(
                request, workspace, project.Image, "bug-analyst", maxClarificationRounds + 1,
                (round, answers, problem) => BugPrompts.Analyst(request, answers, problem, round, maxClarificationRounds + 1),
                text => BugParser.TryParseAnalysis(text, out var value, out var error) ? (value, null) : (null, error),
                BugComments.Analysis,
                value => value.Verdict switch { BugAnalysis.Pass => Next.Done, BugAnalysis.Reject => Next.Stop, _ => Next.Ask },
                failedStatus: Statuses.AnalysisFailed, rejectedStatus: Statuses.Rejected, failureText: "The bug analysis failed");
            if (analysis.Stop is { } stop)
            {
                return stop;
            }
            understood = analysis.Verdict!;
            saved = saved with { BugAnalysis = understood };
            await SaveResumeAsync(request, saved);
        }

        var reproduction = await ReproduceAsync(work, understood);
        if (reproduction.Stop is { } notReproduced)
        {
            return notReproduced;
        }
        var red = reproduction.Verdict!;

        Diagnosis? diagnosis = null;
        var quickReason = WorkMode.QuickReason(request.Labels, project);
        if (quickReason is null)
        {
            var diagnosed = await ConverseAsync<Diagnosis>(
                request, workspace, project.Image, "diagnostician", Rounds(MaxDesignRounds),
                (round, answers, problem) => BugPrompts.Diagnostician(
                    request, understood, red.Reproduction, red.ExitCode, red.Output, answers, problem, round, Rounds(MaxDesignRounds)),
                text => BugParser.TryParseDiagnosis(text, out var value, out var error) ? (value, null) : (null, error),
                BugComments.Diagnosis,
                value => value.Verdict switch { Diagnosis.Pass => Next.Done, Diagnosis.TooLarge => Next.Stop, _ => Next.Ask },
                failedStatus: Statuses.DiagnosisFailed, rejectedStatus: Statuses.NeedsDesign, failureText: "The diagnosis failed");
            if (diagnosed.Stop is { } diagnosisStop)
            {
                return diagnosisStop;
            }
            diagnosis = diagnosed.Verdict;
        }
        else
        {
            await CommentAsync(request, $"**Quick mode:** because {quickReason}, I skip the root-cause analysis and go straight to the fix. The failing test, the project's checks and the reviewers still apply.");
        }

        var brief = new BugBrief(
            red.Reproduction.TestCommand, red.Reproduction.TestFiles, red.ExitCode, red.Output, red.Fingerprints,
            diagnosis?.RootCause, diagnosis?.Locations ?? [], diagnosis?.FixOutline, understood.Severity);

        // The build is judged by the failing test and the project's checks, so the criteria say exactly that.
        IReadOnlyList<CriterionCoverage> criteria =
        [
            new(1, $"The reproduction test (`{string.Join(' ', red.Reproduction.TestCommand)}`) passes without being changed.", CoverageStatus.Missing, [], ""),
            new(2, $"The bug is fixed: expected behaviour is \"{understood.Expected}\".", CoverageStatus.Missing, [], ""),
            new(3, "Every other check of the project still passes.", CoverageStatus.Missing, [], ""),
        ];

        commentsCanStillBeUsed = false;
        return await BuildAsync(work, criteria, null, new TriageDecision(false, []), bug: brief);
    }
}
