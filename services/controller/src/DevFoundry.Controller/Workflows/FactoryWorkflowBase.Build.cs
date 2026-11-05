using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public abstract partial class FactoryWorkflowBase
{
    /// <summary>How many times the developer may try; the project's <c>limits.build_rounds</c>, or the factory's default. Set once the project's settings are read.</summary>
    protected int maxBuildRounds = FactoryDefaults.Standard.BuildRounds;
    protected const int DeveloperTimeoutSeconds = 1800;

    protected static (DeveloperReport? Value, string? Error) ParseDeveloper(string text) =>
        DeveloperParser.TryParse(text, out var report, out var error) ? (report, null) : (null, error);

    /// <summary>
    /// Build stage. Each round the developer works in the writable workspace, then fixed checks decide:
    /// protected paths, symlinks, size and secrets first, then the project's own checks. All pass → the
    /// factory (not the agent) commits, pushes and opens the pull request. Anything else goes back to the
    /// developer as feedback, up to a fixed number of rounds.
    /// </summary>
    protected async Task<FeatureResult> BuildAsync(
        FeatureRequest request, WorkspaceInfo workspace, ProjectConfig project,
        IReadOnlyList<CriterionCoverage> remaining, ArchitectDesign? design, TriageDecision triage, string? quickReason = null, BugBrief? bug = null)
    {
        string? feedback = null;
        DeveloperReport? report = null;
        IReadOnlyList<GateResult> passed = [];
        IReadOnlyList<(string Role, ReviewReport Report)> reviews = [];
        QaReport? qa = null;
        string? uxNote = null;
        var passedRound = 0;

        for (var round = 1; round <= maxBuildRounds && report is null; round++)
        {
            if (ticketClosed)
            {
                return new FeatureResult("closed");
            }

            var feedbackForRound = feedback;
            var outcome = await RunAgentAsync<DeveloperReport>(
                "developer", project.Image, workspace,
                problem => DeveloperPrompt.Build(request, remaining, design, project.Gates, Combine(feedbackForRound, problem), round, maxBuildRounds, bug, project.Rules),
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

            if (bug is not null && await CheckReproductionTestUntouchedAsync(project.Image, workspace, bug) is { } tampered)
            {
                feedback = tampered;
                await CommentAsync(request, $"Build round {round}: {TicketComments.Inert(Short(tampered))}");
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

            var (ruleBlock, ruleNotes) = await CheckRulesAsync(workspace, project);
            if (ruleBlock is not null)
            {
                feedback = ruleBlock;
                await CommentAsync(request, $"Build round {round}: the project's checks passed, but {TicketComments.Inert(Short(ruleBlock))}");
                continue;
            }

            // A bug fix is proven by the reproduction test: it failed before, so it must pass now.
            if (bug is not null && await CheckReproductionPassesAsync(project.Image, workspace, bug) is { } stillFailing)
            {
                feedback = stillFailing;
                await CommentAsync(request, $"Build round {round}: the project's checks passed, but {TicketComments.Inert(Short(stillFailing))}");
                continue;
            }

            var skipQa = quickReason is not null || bug is not null;
            await CommentAsync(request, $"Build round {round}: {BuildFeedback.Summary(results)}. Asking the reviewers{(skipQa ? "" : " and QA")}.");
            var review = await ReviewAsync(request, workspace, project, remaining, design, after, results, skipQa, ruleNotes: ruleNotes);
            if (review.Stop is { } reviewStop)
            {
                return reviewStop;
            }
            if (review.Feedback is not null)
            {
                feedback = review.Feedback;
                continue;
            }

            report = developer;
            passed = results;
            passedRound = round;
            reviews = review.Reports;
            qa = review.Qa;
            uxNote = review.UxNote;
        }

        if (report is null)
        {
            await CommentAsync(request,
                $"I could not get the work through the checks and the review in {maxBuildRounds} rounds, so nothing was published. " +
                $"Last problem: {TicketComments.Inert(Short(feedback ?? "unknown"))} A person needs to take over.");
            return new FeatureResult("build_failed", triage.NeedsArchitect);
        }

        var title = PullRequestText.Title(request);
        PublishResult? published = null;
        PullRequestInfo pullRequest;
        try
        {
            published = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.PublishAsync(new PublishRequest(
                    workspace.Name, $"{title} (#{request.IssueNumber})\n\nBuilt by the factory for {request.Owner}/{request.Repo}#{request.IssueNumber}.\n\n{TicketComments.Inert(report.Summary)}",
                    RecordSpec(request, $"Published as a pull request after every check and review passed (build round {passedRound})."))),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(5), RetryPolicy = new RetryPolicy { MaximumAttempts = 3 } });
            if (published is null)
            {
                await CommentAsync(request, "The work produced no changes to publish.");
                return new FeatureResult("no_changes", triage.NeedsArchitect);
            }
            pullRequest = await Workflow.ExecuteActivityAsync(
                (ForgejoActivities activities) => activities.CreatePullRequestAsync(new PullRequestRequest(
                    request.Owner, request.Repo, published.Branch, workspace.DefaultBranch, title,
                    PullRequestText.Body(request, report, remaining, design, passed, passedRound, reviews, qa, uxNote, quickReason, bug))),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(60), RetryPolicy = new RetryPolicy { MaximumAttempts = 3 } });
        }
        catch (ActivityFailureException error)
        {
            var reason = TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message));
            if (published is not null)
            {
                // The branch is already on the remote; only opening the pull request failed. Nothing needs saving.
                await CommentAsync(request,
                    $"The work passed every check and review and is on the branch `{published.Branch}` ({published.FileCount} file(s)), " +
                    $"but opening the pull request failed: {reason} Open one from that branch by hand.");
                return new FeatureResult("publish_failed", triage.NeedsArchitect, WorkBranch: published.Branch);
            }
            await CommentAsync(request, $"Publishing failed and needs a person to look at it: {reason}");
            return new FeatureResult("publish_failed", triage.NeedsArchitect);
        }

        await CommentAsync(request, $"**Pull request opened:** {pullRequest.Url}\n\n{published.FileCount} file(s) on branch `{published.Branch}`. A person must review and merge it.");
        return new FeatureResult("pr_opened", triage.NeedsArchitect, pullRequest.Url);
    }

    protected static string? Combine(string? feedback, string? unusableAnswer) =>
        unusableAnswer is null ? feedback
        : (feedback is null ? "" : feedback + "\n\n") + $"Your previous reply could not be used: {unusableAnswer}. Reply again with exactly one JSON object in the required shape.";

    protected static Task<ChangeReport> DetectChangesAsync(WorkspaceInfo workspace) =>
        Workflow.ExecuteActivityAsync(
            (WorkspaceActivities activities) => activities.DetectChangesAsync(workspace.Name),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(3), RetryPolicy = new RetryPolicy { MaximumAttempts = 2 } });

    /// <summary>Runs one project check in the sandbox without model access. A check that cannot run counts as failed.</summary>
    protected async Task<GateResult> RunGateAsync(string image, WorkspaceInfo workspace, Gate gate)
    {
        var started = Workflow.UtcNow;
        var commandText = string.Join(' ', gate.Command);
        try
        {
            var result = await Workflow.ExecuteActivityAsync<SandboxRunResult>(
                SandboxNames.ActivityName,
                // The project's own checks may restore packages, but they never get the model login or the model provider.
                [new SandboxRunRequest(image, workspace.Name, WorkspaceWritable: true, gate.Command, gate.TimeoutSeconds, Network: SandboxNetworks.Packages, Role: "gate")],
                new ActivityOptions
                {
                    TaskQueue = SandboxNames.DefaultTaskQueue,
                    StartToCloseTimeout = TimeSpan.FromSeconds(gate.TimeoutSeconds + 300),
                    HeartbeatTimeout = TimeSpan.FromSeconds(30),
                    RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
                });
            var output = string.Join('\n', new[] { result.Stdout, result.Stderr }.Where(part => part.Length > 0));
            var gatePassed = result.ExitCode == 0 && !result.TimedOut;
            await RecordAsync(workspace, "gate", $"gate-{gate.Name}", $"check: {gate.Name}", 1, started, gatePassed ? "passed" : "failed", result.ExitCode, result.TimedOut, commandText, output, null);
            return new GateResult(gate.Name, gatePassed, result.ExitCode, result.TimedOut, output);
        }
        catch (ActivityFailureException error)
        {
            var reason = $"the check could not run: {error.InnerException?.Message ?? error.Message}";
            await RecordAsync(workspace, "gate", $"gate-{gate.Name}", $"check: {gate.Name}", 1, started, "could not run", -1, false, commandText, "", reason);
            return new GateResult(gate.Name, false, -1, false, reason);
        }
    }

    protected sealed record ReviewOutcome(
        FeatureResult? Stop, string? Feedback, IReadOnlyList<(string Role, ReviewReport Report)> Reports, QaReport? Qa, string? UxNote = null);

    protected static (ReviewReport? Value, string? Error) ParseReview(string text) =>
        ReviewParser.TryParse(text, out var report, out var error) ? (report, null) : (null, error);

    protected static (QaReport? Value, string? Error) ParseQa(string text, IReadOnlyList<string> criteria) =>
        QaParser.TryParse(text, criteria, out var report, out var error) ? (report, null) : (null, error);

    /// <summary>
    /// Review stage, after the automated checks pass. The project's reviewers, the UX review (when the rules call for it) and then QA look at the change
    /// without a shell (the diff is given to them), and blocking findings or unmet criteria go back to the
    /// developer. A review that cannot be completed stops the run: nothing is published unreviewed.
    /// </summary>
    protected async Task<ReviewOutcome> ReviewAsync(
        FeatureRequest request, WorkspaceInfo workspace, ProjectConfig project, IReadOnlyList<CriterionCoverage> remaining,
        ArchitectDesign? design, ChangeReport changes, IReadOnlyList<GateResult> gates, bool skipQa = false,
        IReadOnlyList<CriterionCoverage>? qaCriteria = null, string? ruleNotes = null)
    {
        var diff = await Workflow.ExecuteActivityAsync(
            (WorkspaceActivities activities) => activities.DiffAsync(workspace.Name),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(3), RetryPolicy = new RetryPolicy { MaximumAttempts = 2 } });
        var gateSummary = BuildFeedback.Summary(gates) + (ruleNotes is null ? "" : "\n\n" + ruleNotes);
        var checks = ReviewPrompt.Describe(project.Gates);
        var reports = new List<(string Role, ReviewReport Report)>();

        foreach (var role in project.Reviewers)
        {
            var (review, stop) = await RunReviewAsync(
                request, workspace, project, role,
                problem => ReviewPrompt.Build(request, remaining, design, changes, diff, gateSummary, problem, checks: checks));
            if (stop is not null)
            {
                return stop;
            }
            reports.Add((role, review!));
        }

        // UX review: only when the project has a UI, the change touches it, and the UI change is medium or large.
        // The decision is made by counting (UiChange), not by an agent, and a skipped review is noted on the pull request.
        string? uxNote = null;
        if (project.Ui is { } uiConfig)
        {
            var ui = UiChange.Classify(changes.Files, diff, uiConfig);
            if (ui.ShouldReview)
            {
                var uiChanges = new ChangeReport(changes.Files.Where(file => ui.Files.Contains(file.Path)).ToList(), []);
                var uiDiff = UnifiedDiff.Only(diff, ui.Files);
                var others = changes.Files.Count - uiChanges.Files.Count;
                var (review, stop) = await RunReviewAsync(
                    request, workspace, project, "ux",
                    problem => ReviewPrompt.Build(request, remaining, design, uiChanges, uiDiff, gateSummary, problem, others, checks));
                if (stop is not null)
                {
                    return stop;
                }
                reports.Add(("ux", review!));
            }
            else
            {
                uxNote = ui.Size == UiSize.None ? "no UI files changed" : $"the UI change is small ({ui.Describe()})";
            }
        }

        if (skipQa)
        {
            return new ReviewOutcome(null, ReviewFeedback.From(reports, null), reports, null, uxNote);
        }

        var criteria = (qaCriteria ?? remaining).Select(criterion => criterion.Criterion).ToList();
        var qaOutcome = await RunAgentAsync<QaReport>(
            "qa", project.Image, workspace,
            problem => QaPrompt.Build(request, criteria, changes, diff, gateSummary, problem, checks),
            text => ParseQa(text, criteria));
        if (ticketClosed)
        {
            return Stopped("closed");
        }
        if (qaOutcome.NotSignedIn)
        {
            await CommentAsync(request, NotSignedInMessage);
            return Stopped("blocked");
        }
        if (qaOutcome.Value is not { } claimed)
        {
            await CommentAsync(request, $"The QA check failed and needs a person to look at it: {TicketComments.Inert(Short(qaOutcome.Problem ?? "unknown problem"))} Nothing was published.");
            return Stopped("review_failed");
        }

        // QA may only call a criterion met if the files it cites exist.
        var qa = QaParser.Verify(claimed, await FindExistingFilesAsync(workspace, project.Image, QaParser.PathsToCheck(claimed)));
        await CommentAsync(request, TicketComments.Qa(qa));

        return new ReviewOutcome(null, ReviewFeedback.From(reports, qa), reports, qa, uxNote);
    }

    /// <summary>
    /// The project's own rules (<c>rules</c> in project.json) against the lines the change adds: what blocks goes back to the developer,
    /// what only warns goes to the reviewers. Nothing to do, and no extra activity, for a project without rules.
    /// </summary>
    protected async Task<(string? Block, string? Warnings)> CheckRulesAsync(WorkspaceInfo workspace, ProjectConfig project)
    {
        if (project.Rules is not { Count: > 0 } rules)
        {
            return (null, null);
        }
        var diff = await Workflow.ExecuteActivityAsync(
            (WorkspaceActivities activities) => activities.DiffAsync(workspace.Name),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(3), RetryPolicy = new RetryPolicy { MaximumAttempts = 2 } });
        var hits = ProjectRules.Check(rules, diff);
        return (ProjectRules.BlockingFeedback(hits), ProjectRules.WarningNotes(hits));
    }

    /// <summary>Runs one reviewing role and posts its review. A review that cannot be completed stops the run.</summary>
    protected async Task<(ReviewReport? Review, ReviewOutcome? Stop)> RunReviewAsync(
        FeatureRequest request, WorkspaceInfo workspace, ProjectConfig project, string role, Func<string?, string> prompt)
    {
        var outcome = await RunAgentAsync<ReviewReport>(role, project.Image, workspace, prompt, ParseReview);
        if (ticketClosed)
        {
            return (null, Stopped("closed"));
        }
        if (outcome.NotSignedIn)
        {
            await CommentAsync(request, NotSignedInMessage);
            return (null, Stopped("blocked"));
        }
        if (outcome.Value is not { } review)
        {
            await CommentAsync(request, $"The {role} review failed and needs a person to look at it: {TicketComments.Inert(Short(outcome.Problem ?? "unknown problem"))} Nothing was published.");
            return (null, Stopped("review_failed"));
        }
        await CommentAsync(request, TicketComments.Review(role, review));
        return (review, null);
    }

    protected static ReviewOutcome Stopped(string status) => new(new FeatureResult(status), null, [], null);
}
