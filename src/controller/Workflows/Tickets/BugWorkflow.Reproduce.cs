using Temporalio.Workflows;

namespace AiSdlcFactory.Controller;

/// <summary>A reproduction the factory itself has confirmed: the test failed on the unchanged code.</summary>
public sealed record ConfirmedReproduction(
    Reproduction Reproduction, long ExitCode, string Output, IReadOnlyDictionary<string, string> Fingerprints);

public sealed partial class BugWorkflow
{
    /// <summary>
    /// The reproducer writes a failing test; the factory then checks, without trusting the agent, that only test files
    /// changed and that the test really fails on the unchanged code. A test that passes, or a command that cannot run,
    /// goes back to the reproducer with the reason, a bounded number of times.
    /// </summary>
    private async Task<StageResult<ConfirmedReproduction>> ReproduceAsync(WorkContext work, BugAnalysis analysis)
    {
        var (request, workspace, project) = work;
        string? feedback = null;
        for (var attempt = 1; attempt <= MaxReproductionAttempts; attempt++)
        {
            var feedbackForAttempt = feedback;
            var written = await ConverseAsync<Reproduction>(
                request, workspace, project.Image, "reproducer", Rounds(MaxReproductionRounds),
                (round, answers, problem) => BugPrompts.Reproducer(
                    request, analysis, answers, Combine(feedbackForAttempt, problem), round, Rounds(MaxReproductionRounds), project.Gates),
                text => BugParser.TryParseReproduction(text, out var value, out var error) ? (value, null) : (null, error),
                BugComments.Reproduction,
                value => value.Verdict == Reproduction.Reproduced ? Next.Done : Next.Ask,
                failedStatus: Statuses.ReproductionFailed, rejectedStatus: Statuses.CannotReproduce, failureText: "The reproduction failed",
                writable: true);
            if (written.Stop is { } stop)
            {
                return new StageResult<ConfirmedReproduction>(stop, null);
            }
            var reproduction = written.Verdict!;

            var problem = await ProblemWithTestChangesAsync(workspace, reproduction);
            if (problem is null)
            {
                var result = await RunCommandAsync(project.Image, workspace, reproduction.TestCommand, 600, SandboxNetworks.Packages, true, "red-check");
                if (Inconclusive(result))
                {
                    problem = $"The test command `{string.Join(' ', reproduction.TestCommand)}` could not be run ({(result is null ? "the sandbox failed" : result.TimedOut ? "it timed out" : $"exit code {result.ExitCode}, usually a wrong command or a missing tool")}). Give the exact command that runs the test from the repository root.";
                }
                else if (result!.ExitCode == 0)
                {
                    problem = $"The test passes on the unchanged code, so it does not show the bug. Make it fail because of the behaviour in the ticket (a wrong result, not an error in the test itself). Output:\n{Tail(CombinedOutput(result), 1200)}";
                }
                else if (RedCheck.DidNotRunTests(reproduction.TestCommand, result.ExitCode, CombinedOutput(result)) is { } notRun)
                {
                    problem = $"The test command stopped before it could show the bug: {notRun}. That does not prove the bug: it goes away as soon as the missing code exists, whatever that code does. Make the test build and run, and fail because of a wrong result (an assertion), not an error. Output:\n{Tail(CombinedOutput(result), 1200)}";
                }
                else
                {
                    var output = CombinedOutput(result);
                    await RecordAsync(workspace, new RecordStep("gate", "red-check", "check: reproduction fails", 1, Workflow.UtcNow, "failed as required", string.Join(' ', reproduction.TestCommand)) with { ExitCode = result.ExitCode, Output = output });
                    await CommentAsync(request, BugComments.Red(reproduction.TestCommand, result.ExitCode, output));
                    var prints = await FingerprintAsync(project.Image, workspace, reproduction.TestFiles);
                    return new StageResult<ConfirmedReproduction>(null, new ConfirmedReproduction(reproduction, result.ExitCode, output, prints));
                }
            }

            feedback = problem;
            await CommentAsync(request, $"Reproduction attempt {attempt} was not accepted: {TicketComments.Inert(Short(problem))}");
        }

        await CommentAsync(request,
            $"I could not produce a test that fails because of this bug in {MaxReproductionAttempts} attempts, so nothing was built. " +
            "Add exact steps, input or a stack trace to the ticket, then close and reopen it.");
        return new StageResult<ConfirmedReproduction>(new WorkResult(Statuses.CannotReproduce), null);
    }

    /// <summary>The reproducer may only add or change the test files it names; anything else is sent back.</summary>
    private async Task<string?> ProblemWithTestChangesAsync(WorkspaceInfo workspace, Reproduction reproduction)
    {
        var changes = await DetectChangesAsync(workspace);
        if (changes.Violations.Count > 0)
        {
            return BuildFeedback.FromViolations(changes.Violations);
        }
        if (changes.Files.Count == 0)
        {
            return "No files changed. Write the failing test in the workspace.";
        }
        var stray = changes.Files.Where(file => !reproduction.TestFiles.Contains(file.Path)).Select(file => file.Path).ToList();
        if (stray.Count > 0)
        {
            return $"You changed files that are not test files you listed in \"test_files\": {string.Join(", ", stray.Take(5))}. Change only test files, and list every one of them.";
        }
        if (changes.Files.Any(file => file.Kind == FileChange.Deleted))
        {
            return "Do not delete files. Only add or change tests.";
        }
        return null;
    }
}
