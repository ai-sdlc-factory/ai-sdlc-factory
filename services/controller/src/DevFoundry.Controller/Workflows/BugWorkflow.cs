using Temporalio.Workflows;

namespace DevFoundry.Controller;

/// <summary>
/// One run per bug ticket. The aim is a fix that is proven by a test that fails before and passes after:
/// bug analyst, reproducer (writes a failing test), the factory's own "red" check (the test must fail on the unchanged
/// code), diagnostician (root cause), then the shared build with the developer, the project's checks, the "green" check,
/// and the reviewers. QA is replaced by the red/green proof. Quick mode skips the diagnosis.
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
    public Task<FeatureResult> RunAsync(FeatureRequest request) => RunPipelineAsync(request, "bug", StagesAsync);

    private async Task<FeatureResult> StagesAsync(FeatureRequest request, WorkspaceInfo workspace, ProjectConfig project)
    {
        var analysis = await ConverseAsync<BugAnalysis>(
            request, workspace, project.Image, "bug-analyst", MaxClarificationRounds,
            (round, answers, problem) => BugPrompts.Analyst(request, answers, problem, round, MaxClarificationRounds),
            text => BugParser.TryParseAnalysis(text, out var value, out var error) ? (value, null) : (null, error),
            BugComments.Analysis,
            value => value.Verdict switch { BugAnalysis.Pass => Next.Done, BugAnalysis.Reject => Next.Stop, _ => Next.Ask },
            failedStatus: "analysis_failed", rejectedStatus: "rejected", failureText: "The bug analysis failed");
        if (analysis.Stop is { } stop)
        {
            return stop;
        }
        var understood = analysis.Verdict!;

        var reproduction = await ReproduceAsync(request, workspace, project, understood);
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
                request, workspace, project.Image, "diagnostician", MaxDesignRounds,
                (round, answers, problem) => BugPrompts.Diagnostician(
                    request, understood, red.Reproduction, red.ExitCode, red.Output, answers, problem, round, MaxDesignRounds),
                text => BugParser.TryParseDiagnosis(text, out var value, out var error) ? (value, null) : (null, error),
                BugComments.Diagnosis,
                value => value.Verdict switch { Diagnosis.Pass => Next.Done, Diagnosis.TooLarge => Next.Stop, _ => Next.Ask },
                failedStatus: "diagnosis_failed", rejectedStatus: "needs_design", failureText: "The diagnosis failed");
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
        return await BuildAsync(request, workspace, project, criteria, null, new TriageDecision(false, []), bug: brief);
    }
}
