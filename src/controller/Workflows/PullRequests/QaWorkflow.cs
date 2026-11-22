using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AiSdlcFactory.Controller;

/// <summary>
/// A deeper test of a finished change, started by a person with <c>/qa</c> on the ticket or on the factory's pull request. A tester agent tries the
/// application from the outside (its API and, if the project has one, its UI), and writes end-to-end tests for what it finds under the project's tests
/// folder. The factory then runs those suites itself, in a sandbox without the model login, so pass and fail are facts. Tests that all pass are pushed to
/// the pull request as one commit; otherwise they are saved to a branch. A UX review reads what the UI tests saw. The report goes on the ticket; a person
/// decides what to do with it, and <c>/fix qa</c> hands its findings to the revision flow.
/// </summary>
[Workflow("QaWorkflow")]
public sealed partial class QaWorkflow : FactoryWorkflowBase
{
    private const int MaxAttempts = 3;

    /// <summary>Set when the change is all UI and the project has no ui suite (see <see cref="QaUiGap"/>); the report repeats it.</summary>
    private string? uiGap;

    /// <summary>A person commented <c>/qa go</c>: the go-ahead a run waits for when its estimate is over <c>limits.confirm_over</c>.</summary>
    [WorkflowSignal("qa_go")]
    public Task OnGoAsync()
    {
        qaConfirmed = true;
        return Task.CompletedTask;
    }

    [WorkflowSignal("ticket_closed")]
    public Task OnTicketClosedAsync()
    {
        MarkTicketClosed();
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<WorkResult> RunAsync(QaRequest qa)
    {
        var placeholder = qa.ToWorkRequest($"QA of #{qa.Number}");
        QaContext context;
        try
        {
            context = await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.ReadQaContextAsync(qa),
                ActivityPolicy.Local(TimeSpan.FromMinutes(2), 3));
        }
        catch (ActivityFailureException error)
        {
            await CommentAsync(placeholder, $"I could not read the pull request, so I did no QA: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))}");
            return new WorkResult(Statuses.Error);
        }

        if (QaRules.Refuse(qa, context) is { } refusal)
        {
            if (refusal.Message is not null)
            {
                await CommentAsync(placeholder, refusal.Message);
            }
            return new WorkResult(refusal.Status);
        }

        var pull = context.Pull!;
        return await RunPipelineAsync(
            qa.ToWorkRequest(pull.Title), "QA",
            work => StagesAsync(work, qa, pull, context.Changes),
            workspaceFor: request => new WorkspaceRequest(request.Owner, request.Repo, request.WorkflowId, pull.HeadRef, ExistingBranch: true),
            startMessage: $"Starting a QA run on pull request #{pull.Number}. The tester will try the application and write end-to-end tests; this can take a while.");
    }

    private async Task<WorkResult> StagesAsync(WorkContext work, QaRequest qa, PullRequestDetails pull, PullChanges? changes)
    {
        var (request, workspace, project) = work;
        commentsCanStillBeUsed = false;

        if (project.Verify is not { } verify)
        {
            await CommentAsync(request, TicketComments.QaNotConfigured);
            return new WorkResult(Statuses.QaNotConfigured);
        }
        var suites = qa.Suite is null ? verify.Suites.ToList() : verify.Suites.Where(suite => suite.Name == qa.Suite).ToList();
        if (suites.Count == 0)
        {
            await CommentAsync(request, TicketComments.QaSuiteMissing(qa.Suite!, verify.Suites.Select(s => s.Name)));
            return new WorkResult(Statuses.QaNotConfigured);
        }

        // Before any tokens are spent (the tester is the expensive part); a person said /qa to start this, and may have asked to be asked again when it is costly.
        if (await ConfirmExpensiveRunAsync(request) is { } notGoingOn)
        {
            return notGoingOn;
        }

        var image = verify.Image ?? project.Image;
        var criteria = PullRequestCriteria.FromBody(pull.Body);

        // Said before the tester spends a run, and again in the report, so it is not lost when the report is read days later.
        uiGap = QaUiGap.Describe(changes, project.Ui, verify, qa.Suite);
        if (uiGap is not null)
        {
            await CommentAsync(request, TicketComments.QaUiGap(uiGap, qa.Mode));
        }

        TesterReport? tester = null;
        IReadOnlyList<QaSuiteRun> runs = [];
        string? feedback = null;
        string? lastRefusal = null;
        var attempts = 0;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            attempts = attempt;
            var feedbackNow = feedback;
            var outcome = await RunAgentAsync<TesterReport>(
                "e2e-tester", image, workspace,
                problem => TesterPrompt.Build(pull, criteria, verify, suites, feedbackNow, attempt, MaxAttempts, problem, qa.Mode, qa.Note, changes),
                TesterParser.Parse, writable: true, timeoutSeconds: DeveloperTimeoutSeconds);
            if (outcome.NotSignedIn)
            {
                await CommentAsync(request, NotSignedInMessage);
                return new WorkResult(Statuses.Blocked);
            }
            if (outcome.Value is not { } report)
            {
                await CommentAsync(request, $"The end-to-end tester failed and needs a person to look at it: {outcome.Describe()}");
                return new WorkResult(outcome.FailedStatus(Statuses.QaFailed));
            }
            tester = report;
            if (report.Verdict == TesterReport.Blocked)
            {
                await CommentAsync(request, TicketComments.QaBlocked(report.Summary, qa.Mode));
                return new WorkResult(Statuses.QaBlocked);
            }

            var made = await DropOversizedFilesAsync(request, workspace, attempt, await DetectChangesAsync(workspace));
            feedback = ProblemWithTestChanges(made, verify);
            if (feedback is not null)
            {
                await CommentAsync(request, $"QA attempt {attempt}: the tester's changes were not accepted: {TicketComments.Inert(Short(feedback))}");
                runs = [];
                if (feedback == lastRefusal)
                {
                    // The same refusal twice in a row: another attempt would cost tokens and end the same way.
                    await CommentAsync(request, "The tester was refused for the same reason twice, so I stop here instead of spending another attempt.");
                    break;
                }
                lastRefusal = feedback;
                continue;
            }
            lastRefusal = null;

            var results = new List<QaSuiteRun>();
            foreach (var suite in suites)
            {
                var run = await RunSuiteAsync(image, workspace, verify, suite);
                if (run is null)
                {
                    await CommentAsync(request, $"QA attempt {attempt}: the {suite.Name} suite could not run because of the factory itself.{TicketComments.RetryHint}");
                    return new WorkResult(Statuses.InfrastructureFailed);
                }
                results.Add(run);
                if (run.AppDidNotStart)
                {
                    break; // the other suites would meet the same wall
                }
            }
            runs = results;
            await CommentAsync(request, $"QA attempt {attempt}: " + string.Join(", ", results.Select(r => $"{r.Suite} {(r.AppDidNotStart ? "app did not start" : r.Passed ? "passed" : "failed")}")) + ".");

            var nothingMoreToFix = results.All(r => r.Passed) || results.Any(r => r.AppDidNotStart);
            var testerBlamesTheApplication = attempt > 1 && report.BlamesOnlyTheApplication;
            if (nothingMoreToFix || testerBlamesTheApplication || attempt == MaxAttempts)
            {
                break;
            }
            feedback = SuiteFeedback(results);
        }

        return await FinishAsync(work, qa, pull, verify, image, criteria, tester, runs, attempts, feedback);
    }

    /// <summary>
    /// New files too large to publish or check (an application log, a recording) are removed here and named on the ticket, instead of being sent back to the tester,
    /// which would cost an attempt. Returns the changes as they are afterwards.
    /// </summary>
    private async Task<ChangeReport> DropOversizedFilesAsync(WorkRequest request, WorkspaceInfo workspace, int attempt, ChangeReport made)
    {
        if (made.Oversized.Count == 0)
        {
            return made;
        }
        var dropped = await Workflow.ExecuteActivityAsync(
            (WorkspaceActivities activities) => activities.RemoveOversizedAsync(new RemoveOversizedRequest(workspace.Name, made.Oversized)),
            ActivityPolicy.Local(TimeSpan.FromMinutes(3), 2));
        if (dropped.Count == 0)
        {
            return made;
        }
        await CommentAsync(request, $"QA attempt {attempt}: left out {dropped.Count} file(s) the tester wrote that are too large to publish or check ({string.Join(", ", dropped.Take(5).Select(TicketComments.Inert))}). Logs and recordings belong in /tmp.");
        return await DetectChangesAsync(workspace);
    }

    /// <summary>The tester may change only files under the tests folder. Anything else, a deletion, a secret or a protected file goes back to it.</summary>
    private static string? ProblemWithTestChanges(ChangeReport changes, VerifyConfig verify)
    {
        if (changes.Violations.Count > 0)
        {
            return BuildFeedback.FromViolations(changes.Violations);
        }
        if (changes.Files.Count == 0)
        {
            return $"No files changed. Write the tests under {verify.Tests}/.";
        }
        var outside = changes.Files.Where(file => !file.Path.StartsWith(verify.Tests + "/", StringComparison.Ordinal)).Select(file => file.Path).ToList();
        if (outside.Count > 0)
        {
            return $"You changed files outside {verify.Tests}/: {string.Join(", ", outside.Take(5))}. Undo those changes; only files under {verify.Tests}/ may change.";
        }
        if (changes.Files.Any(file => file.Kind == FileChange.Deleted))
        {
            return "Do not delete files. Only add or update tests.";
        }
        return null;
    }

    private static string SuiteFeedback(IReadOnlyList<QaSuiteRun> runs)
    {
        var text = new System.Text.StringBuilder();
        foreach (var run in runs.Where(r => !r.Passed))
        {
            text.AppendLine($"The {run.Suite} suite {(run.TimedOut ? "timed out" : $"failed with exit code {run.ExitCode}")}. The end of its output:");
            text.AppendLine(run.Tail);
        }
        text.AppendLine("For every failing test say in \"assessments\" whether it is an application_bug (the application is wrong: keep the test) or a test_problem (your test is wrong: fix it).");
        return text.ToString().TrimEnd();
    }

    /// <summary>Runs one suite with the application started. Null means the sandbox itself failed (the factory's problem, not the code's).</summary>
    private async Task<QaSuiteRun?> RunSuiteAsync(string image, WorkspaceInfo workspace, VerifyConfig verify, Gate suite)
    {
        var started = Workflow.UtcNow;
        var command = VerifyScript.Command(verify, suite);
        var seconds = suite.TimeoutSeconds + (verify.Start is null ? 0 : verify.StartupSeconds);
        try
        {
            var result = await Workflow.ExecuteActivityAsync<SandboxRunResult>(
                SandboxNames.ActivityName,
                // The suites may restore packages, but they never get the model login or the model provider.
                [new SandboxRunRequest(image, workspace.Name, WorkspaceWritable: true, command, seconds, Network: SandboxNetworks.Packages, Role: "verify")],
                ActivityPolicy.Sandbox(TimeSpan.FromSeconds(seconds + 300), attempts: 1));
            var output = string.Join('\n', new[] { result.Stdout, result.Stderr }.Where(part => part.Length > 0));
            var appDidNotStart = result.ExitCode is VerifyScript.AppExitedEarly or VerifyScript.AppNotReady;
            var passed = result.ExitCode == 0 && !result.TimedOut;
            await RecordAsync(workspace, RecordStep.Gate($"qa-{suite.Name}", started, passed ? "passed" : "failed", suite.Display) with { ExitCode = result.ExitCode, TimedOut = result.TimedOut, Output = output });
            return new QaSuiteRun(suite.Name, passed, result.ExitCode, result.TimedOut, Tail(output, 1500), appDidNotStart);
        }
        catch (ActivityFailureException error)
        {
            var infrastructure = InfrastructureFailure.Is(error);
            await RecordAsync(workspace, RecordStep.Gate($"qa-{suite.Name}", started, "could not run", suite.Display) with
            {
                ExitCode = -1, Problem = infrastructure ? InfrastructureFailure.Describe(error) : error.InnerException?.Message ?? error.Message,
            });
            if (infrastructure)
            {
                return null;
            }
            return new QaSuiteRun(suite.Name, false, -1, false, Short(error.InnerException?.Message ?? error.Message), false);
        }
    }
}
