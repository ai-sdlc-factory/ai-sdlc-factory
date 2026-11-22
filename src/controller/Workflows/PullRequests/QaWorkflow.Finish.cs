using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AiSdlcFactory.Controller;

public sealed partial class QaWorkflow
{
    /// <summary>
    /// After the last attempt: read what the UI tests saw, remove it so it is never pushed, have the UX review look at it, then push the tests (all
    /// suites passed) or save them to a branch (otherwise), and post the report.
    /// </summary>
    private async Task<WorkResult> FinishAsync(
        WorkContext work, QaRequest qa, PullRequestDetails pull, VerifyConfig verify, string image, IReadOnlyList<CriterionCoverage> criteria,
        TesterReport? tester, IReadOnlyList<QaSuiteRun> runs, int attempts, string? lastProblem)
    {
        var (request, workspace, project) = work;
        var ranUi = runs.Any(run => run.Suite == VerifyConfig.Ui && !run.AppDidNotStart);
        var evidence = ranUi ? await ReadEvidenceAsync(image, workspace, verify) : "";
        if (runs.Any(run => run.Suite == VerifyConfig.Ui))
        {
            await RunCommandAsync(image, workspace, VerifyScript.RemoveEvidence(verify), 60, SandboxNetworks.None, true, "evidence");
        }

        ReviewReport? ux = null;
        string? uxNote = null;
        if (ranUi)
        {
            if (evidence.Length == 0)
            {
                uxNote = $"Skipped: the ui tests left no page text in {verify.EvidenceDirectory}/ for the review to read.";
            }
            else
            {
                (ux, uxNote) = await ReviewUiAsync(work, image, criteria, runs, evidence);
            }
        }
        else if (runs.Any(run => run.Suite == VerifyConfig.Ui))
        {
            uxNote = "Skipped: the application did not start for the ui tests.";
        }

        var tests = await PublishOrSaveAsync(work, pull, verify, runs, lastProblem);
        var input = new QaReportInput(
            pull, runs, tester, ux, uxNote, tests, attempts, TotalUsage,
            RecordSpec(request, "QA report") is null ? "" : "The full record of this run is committed with the tests.", qa.Mode, qa.Note, uiGap);
        await CommentAsync(request, QaReportComment.Build(input));
        return new WorkResult(Statuses.QaReported, WorkBranch: tests.Kind == QaTestsOutcome.Pushed ? pull.HeadRef : tests.Kind == QaTestsOutcome.Saved ? tests.Reference : null);
    }

    private async Task<string> ReadEvidenceAsync(string image, WorkspaceInfo workspace, VerifyConfig verify)
    {
        var result = await RunCommandAsync(image, workspace, VerifyScript.ReadEvidence(verify), 60, SandboxNetworks.None, false, "evidence");
        return result is { ExitCode: 0 } ? result.Stdout.Trim() : "";
    }

    /// <summary>The UX review of what the UI tests saw. A review that cannot be completed is noted in the report and does not stop it.</summary>
    private async Task<(ReviewReport? Review, string? Note)> ReviewUiAsync(
        WorkContext work, string image, IReadOnlyList<CriterionCoverage> criteria, IReadOnlyList<QaSuiteRun> runs, string evidence)
    {
        var (request, workspace, _) = work;
        var changes = await DetectChangesAsync(workspace);
        var summary = "end-to-end tests: " + string.Join(", ", runs.Select(run => $"{run.Suite} {(run.Passed ? "passed" : "failed")}"));
        var outcome = await RunAgentAsync<ReviewReport>(
            "ux", image, workspace,
            problem => ReviewPrompt.Build(
                request, criteria, null, changes, "", summary, problem, evidence: evidence,
                note: "This is a QA run on a finished pull request, not a code review: the diff above is empty or holds only the new tests. " +
                      "Judge the application's user interface from the evidence and from the UI code in the repository."),
            ParseReview);
        if (outcome.Value is { } review)
        {
            return (review, null);
        }
        return (null, $"The UX review could not be completed: {outcome.Describe()}");
    }

    /// <summary>
    /// Tests that all passed are pushed to the pull request as one commit. Anything else is saved to a work-in-progress branch for a person
    /// to look at, so a test that exposes a real problem is not lost and a wrong test never reaches the pull request.
    /// </summary>
    private async Task<QaTestsOutcome> PublishOrSaveAsync(WorkContext work, PullRequestDetails pull, VerifyConfig verify, IReadOnlyList<QaSuiteRun> runs, string? lastProblem)
    {
        var (request, workspace, _) = work;
        if (runs.Count == 0)
        {
            return new QaTestsOutcome(QaTestsOutcome.None, null, 0, lastProblem is null ? "The tester wrote nothing that could be run." : $"The tester's changes were not accepted: {Short(lastProblem)}");
        }

        // Running the suites can write files next to the tests (reports, test results). Those must never reach the pull request.
        string? stray = null;
        if (runs.All(run => run.Passed))
        {
            var after = await DetectChangesAsync(workspace);
            var outside = after.Files.Where(file => !file.Path.StartsWith(verify.Tests + "/", StringComparison.Ordinal)).Select(file => file.Path).ToList();
            if (outside.Count > 0)
            {
                stray = $"The suites wrote files outside {verify.Tests}/ ({string.Join(", ", outside.Take(5))}{(outside.Count > 5 ? ", ..." : "")}). Add them to .gitignore (or make the suite write them under /tmp), then run /qa again.";
            }
        }

        if (runs.All(run => run.Passed) && stray is null)
        {
            try
            {
                var published = await Workflow.ExecuteActivityAsync(
                    (WorkspaceActivities activities) => activities.PublishAsync(new PublishRequest(
                        workspace.Name,
                        $"Add end-to-end tests for #{pull.Number}\n\nWritten by the factory's QA run and run by the factory: every suite passed.",
                        RecordSpec(request, $"QA run on pull request #{pull.Number}: tests pushed."),
                        UpdateExistingBranch: true)),
                    ActivityPolicy.Local(TimeSpan.FromMinutes(5), 3));
                return published is null
                    ? new QaTestsOutcome(QaTestsOutcome.None, null, 0, "The run left no new files to push.")
                    : new QaTestsOutcome(QaTestsOutcome.Pushed, published.CommitSha, published.FileCount, null);
            }
            catch (ActivityFailureException error)
            {
                return new QaTestsOutcome(QaTestsOutcome.None, null, 0, $"Pushing the tests failed: {Short(error.InnerException?.Message ?? error.Message)}");
            }
        }

        try
        {
            var rescue = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.RescueAsync(new RescueRequest(
                    request.WorkflowId,
                    $"WIP: end-to-end tests for #{pull.Number}, not accepted\n\nSaved by the factory's QA run. Not every suite passed, so they were not added to the pull request.",
                    RecordSpec(request, $"QA run on pull request #{pull.Number}: tests saved, not every suite passed."))),
                ActivityPolicy.Local(TimeSpan.FromMinutes(5), 2));
            return rescue.Saved
                ? new QaTestsOutcome(QaTestsOutcome.Saved, rescue.Branch, rescue.FileCount, stray)
                : new QaTestsOutcome(QaTestsOutcome.None, null, 0, rescue.Note);
        }
        catch (ActivityFailureException error)
        {
            return new QaTestsOutcome(QaTestsOutcome.None, null, 0, $"Saving the tests failed: {Short(error.InnerException?.Message ?? error.Message)}");
        }
    }
}
