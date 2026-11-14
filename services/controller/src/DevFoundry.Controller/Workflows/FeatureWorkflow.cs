using Temporalio.Workflows;

namespace DevFoundry.Controller;

/// <summary>
/// One run per feature ticket, following docs/feature-factory.md: the analyst, the coverage check, triage, the
/// architect when triage asks for it, and then the shared build (developer, automated checks, reviewers, acceptance check, pull
/// request). Quick mode skips the coverage check, the design review and the acceptance check.
/// </summary>
[Workflow("FeatureWorkflow")]
public sealed class FeatureWorkflow : FactoryWorkflowBase
{
    [WorkflowSignal("human_reply")]
    public Task OnHumanReplyAsync(HumanReply reply) => AcceptHumanReplyAsync(reply);

    [WorkflowSignal("ticket_closed")]
    public Task OnTicketClosedAsync()
    {
        MarkTicketClosed();
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public Task<WorkResult> RunAsync(WorkRequest request) => RunPipelineAsync(request, "feature", StagesAsync);

    /// <summary>
    /// The stages after intake, in order: analyst, coverage check, triage. Each either stops the
    /// workflow with a status or hands its result to the next one.
    /// </summary>
    private async Task<WorkResult> StagesAsync(WorkContext work)
    {
        var (request, workspace, project) = work;
        var analysis = await AnalyseAsync(request, workspace, project.Image);
        if (analysis.Stop is { } stop)
        {
            return stop;
        }
        var verdict = analysis.Verdict!;

        // Quick mode (chosen by a person, never by an agent) skips the coverage check, the design review and the acceptance check.
        var quickReason = WorkMode.QuickReason(request.Labels, project);
        CoverageReport coverageReport;
        if (quickReason is null)
        {
            var coverage = await CheckCoverageAsync(request, workspace, project.Image, verdict);
            if (coverage.Stop is { } stopped)
            {
                return stopped;
            }
            coverageReport = coverage.Verdict!;
        }
        else
        {
            // Nothing was checked, so every criterion still has to be built.
            coverageReport = new CoverageReport(
                CoverageVerdicts.NotCovered, "The coverage check was skipped (quick mode).",
                verdict.AcceptanceCriteria.Select((criterion, i) => new CriterionCoverage(i + 1, criterion, CoverageStatus.Missing, [], "")).ToList());
        }

        var triage = Triage.Decide(verdict.Complexity, request.Labels ?? []);
        if (quickReason is null)
        {
            await CommentAsync(request, TicketComments.TriageDecision(triage));
        }
        else
        {
            // Only an explicit "needs-architecture" label from a person still gets a design review in quick mode.
            var forced = (request.Labels ?? []).Any(label => string.Equals(label, Triage.ForceLabel, StringComparison.OrdinalIgnoreCase));
            var skipped = triage.NeedsArchitect && !forced ? triage.Reasons : [];
            if (triage.NeedsArchitect && !forced)
            {
                triage = new TriageDecision(false, []);
            }
            await CommentAsync(request, TicketComments.QuickMode(quickReason, skipped));
            if (triage.NeedsArchitect)
            {
                await CommentAsync(request, TicketComments.TriageDecision(triage));
            }
        }

        ArchitectDesign? approved = null;
        if (triage.NeedsArchitect)
        {
            var design = await DesignAsync(request, workspace, project.Image, coverageReport, triage);
            if (design.Stop is { } designStop)
            {
                return designStop;
            }
            approved = design.Verdict;
        }

        // From here no stage asks a question, so a new comment can no longer change the outcome.
        commentsCanStillBeUsed = false;
        return await BuildAsync(work, coverageReport.Remaining.ToList(), approved, triage, quickReason);
    }
}
