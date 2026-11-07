using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

/// <summary>
/// A person reviewed a pull request the factory opened and asked for changes (a review that requests changes, or <c>/fix</c> in a
/// comment). The pull request resolver addresses the comments on the pull request's own branch; the factory then runs the same change
/// checks, project checks and reviewers as for a feature, and pushes one new commit onto the same branch (never forcing, so a person's
/// work is never overwritten) and replies on the pull request. At most three revisions per pull request, and one at a time.
/// </summary>
[Workflow("PullRequestRevisionWorkflow")]
public sealed partial class PullRequestRevisionWorkflow : FactoryWorkflowBase
{
    private RevisionRequest? followUp;

    /// <summary>A review or <c>/fix</c> arrived while this revision was running. It is not worked into this one (the agent already has
    /// its comments); a follow-up revision reads it afterwards.</summary>
    [WorkflowSignal("more_feedback")]
    public Task OnMoreFeedbackAsync(RevisionRequest request)
    {
        followUp = request;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<WorkResult> RunAsync(RevisionRequest revision)
    {
        var result = await RunOnceAsync(revision);
        if (followUp is { } next && result.Status is Statuses.RevisionPushed or Statuses.RevisionReplied)
        {
            // Same workflow id, fresh history; the revision limit still applies, and what was already answered is not read again.
            throw Workflow.CreateContinueAsNewException((PullRequestRevisionWorkflow workflow) => workflow.RunAsync(next));
        }
        return result;
    }

    private async Task<WorkResult> RunOnceAsync(RevisionRequest revision)
    {
        var placeholder = revision.ToWorkRequest($"Pull request #{revision.PullNumber}");
        RevisionContext context;
        try
        {
            context = await Workflow.ExecuteActivityAsync(
                (ForgejoActivities activities) => activities.ReadRevisionContextAsync(revision),
                ActivityPolicy.Local(TimeSpan.FromMinutes(2), 3));
        }
        catch (ActivityFailureException error)
        {
            // Someone asked (a review, or /fix), so silence would look like the factory ignoring them.
            await CommentAsync(placeholder, $"I could not read this pull request, so I did nothing: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))}");
            return new WorkResult(Statuses.Error);
        }

        if (RevisionRules.Refuse(revision, context) is { } refusal)
        {
            if (refusal.Message is not null)
            {
                await CommentAsync(placeholder, refusal.Message);
            }
            return new WorkResult(refusal.Status);
        }

        var pull = context.Pull;
        var comments = RevisionComments.Collect(revision, context.Reviews);
        if (comments.Count == 0)
        {
            await CommentAsync(placeholder, TicketComments.RevisionNothingToDo);
            return new WorkResult(Statuses.NothingToDo);
        }

        var request = revision.ToWorkRequest(pull.Title);
        return await RunPipelineAsync(
            request, "pull request revision",
            work => StagesAsync(work, pull, comments, context.Reviews, context.PriorRevisions),
            workspaceFor: feature => new WorkspaceRequest(feature.Owner, feature.Repo, feature.WorkflowId, pull.HeadRef, ExistingBranch: true),
            startMessage: $"I am working on the {comments.Count} review comment{(comments.Count == 1 ? "" : "s")} on this pull request.");
    }

    private static async Task BestEffortAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ActivityFailureException)
        {
        }
    }

    private static (RevisionReport? Value, string? Error) ParseReport(string text, int count) =>
        RevisionParser.TryParse(text, count, out var report, out var error) ? (report, null) : (null, error);

    private async Task<WorkResult> StagesAsync(
        WorkContext work, PullRequestDetails pull, IReadOnlyList<RevisionComment> comments, IReadOnlyList<ReviewDetails> reviews, int priorRevisions)
    {
        var (request, workspace, project) = work;
        if (priorRevisions >= maxRevisionsPerPullRequest)
        {
            await CommentAsync(request, TicketComments.RevisionLimit(maxRevisionsPerPullRequest));
            return new WorkResult(Statuses.RevisionLimit);
        }

        // No stage here asks a person a question: a comment that cannot be acted on is answered on the pull request instead.
        commentsCanStillBeUsed = false;

        // The reviewers are given the review comments as the "ticket", and each comment as something to be met.
        var reviewRequest = request with
        {
            Title = $"Review comments on pull request #{pull.Number}: {pull.Title}",
            Body = string.Join("\n\n", comments.Select(c => $"[{c.Index}] {c.Where}: {c.Body}")),
        };
        IReadOnlyList<CriterionCoverage> criteria = comments
            .Select(c => new CriterionCoverage(c.Index, $"Review comment {c.Index} ({c.Where}) is addressed: fixed, or answered with a reason: {c.Body}", CoverageStatus.Missing, [], ""))
            .ToList();

        string? feedback = null;
        for (var round = 1; round <= maxBuildRounds; round++)
        {
            var feedbackForRound = feedback;
            var outcome = await RunAgentAsync<RevisionReport>(
                "pr-resolver", project.Image, workspace,
                problem => RevisionPrompts.Build(pull, comments, project.Gates, Combine(feedbackForRound, problem), round, maxBuildRounds, project.Rules),
                text => ParseReport(text, comments.Count), writable: true, timeoutSeconds: DeveloperTimeoutSeconds);
            if (outcome.NotSignedIn)
            {
                await CommentAsync(request, NotSignedInMessage);
                return new WorkResult(Statuses.Blocked);
            }
            if (outcome.Value is not { } report)
            {
                await CommentAsync(request, $"I could not address the review comments and a person needs to look at it: {TicketComments.Inert(Short(outcome.Problem ?? "unknown problem"))}");
                return new WorkResult(Statuses.BuildFailed);
            }
            if (report.Verdict == RevisionReport.Blocked)
            {
                await CommentAsync(request, $"**Revision: I cannot continue**\n\n{TicketComments.Inert(report.Summary)}" +
                    string.Concat(report.Notes.Select(note => $"\n- {TicketComments.Inert(note)}")));
                return new WorkResult(Statuses.BuildBlocked);
            }

            var before = await DetectChangesAsync(workspace);
            var claimedFix = report.Responses.Where(r => r.Status == RevisionResponse.Fixed).Select(r => r.Index).ToList();
            if (before.Violations.Count > 0)
            {
                feedback = BuildFeedback.FromViolations(before.Violations);
                await CommentAsync(request, $"Revision round {round}: the changes were rejected: {TicketComments.Inert(Short(feedback))}");
                continue;
            }
            if (before.Files.Count == 0)
            {
                if (claimedFix.Count > 0)
                {
                    feedback = $"You marked comment {string.Join(", ", claimedFix)} as fixed, but no files in the workspace changed. Make the change, or answer it as answered or declined with a reason.";
                    await CommentAsync(request, $"Revision round {round}: {TicketComments.Inert(Short(feedback))}");
                    continue;
                }
                // Every comment was answered or declined, so there is nothing to push: the answers are the result.
                await CommentAsync(request, RevisionReply.Replied(report, comments));
                return new WorkResult(Statuses.RevisionReplied);
            }

            var results = new List<GateResult>();
            foreach (var gate in project.Gates)
            {
                results.Add(await RunGateAsync(project.Image, workspace, gate));
            }
            var after = await DetectChangesAsync(workspace);
            feedback = BuildFeedback.FromGates(results);
            if (feedback is null && after.Violations.Count > 0)
            {
                feedback = BuildFeedback.FromViolations(after.Violations);
            }
            if (feedback is not null)
            {
                await CommentAsync(request, $"Revision round {round}: {BuildFeedback.Summary(results)}." +
                    (after.Violations.Count > 0 ? $" Also rejected: {TicketComments.Inert(Short(string.Join("; ", after.Violations.Take(3))))}" : ""));
                continue;
            }

            var (ruleBlock, ruleNotes) = await CheckRulesAsync(workspace, project);
            if (ruleBlock is not null)
            {
                feedback = ruleBlock;
                await CommentAsync(request, $"Revision round {round}: the project's checks passed, but {TicketComments.Inert(Short(ruleBlock))}");
                continue;
            }

            await CommentAsync(request, $"Revision round {round}: {BuildFeedback.Summary(results)}. Asking the reviewers.");
            // A larger revision is also checked against what the ticket asked for (the criteria in the pull request body).
            var original = PullRequestCriteria.FromBody(pull.Body);
            var checkOriginal = original.Count > 0 && after.Files.Count >= PullRequestCriteria.QaFromChangedFiles;
            var review = await ReviewAsync(
                work with { Request = reviewRequest }, criteria, null, after, results,
                new ReviewOptions(SkipQa: !checkOriginal, QaCriteria: checkOriginal ? original : null, RuleNotes: ruleNotes));
            if (review.Stop is { } reviewStop)
            {
                return reviewStop;
            }
            if (review.Feedback is not null)
            {
                feedback = review.Feedback;
                continue;
            }

            var message = $"Address review comments on #{pull.Number}\n\n{TicketComments.Inert(report.Summary)}";
            var record = RecordSpec(request, $"Pushed as a revision of pull request #{pull.Number}.");
            PublishResult? published;
            try
            {
                published = await Workflow.ExecuteActivityAsync(
                    (WorkspaceActivities activities) => activities.PublishAsync(new PublishRequest(workspace.Name, message, record, UpdateExistingBranch: true)),
                    ActivityPolicy.Local(TimeSpan.FromMinutes(5), 3));
            }
            catch (ActivityFailureException error)
            {
                await CommentAsync(request, $"The changes passed every check, but pushing them failed: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))} Ask again with `/fix` once the branch is stable.");
                return new WorkResult(Statuses.PublishFailed);
            }
            if (published is null)
            {
                await CommentAsync(request, "The work produced no changes to push.");
                return new WorkResult(Statuses.NoChanges);
            }

            var notes = review.Reports.SelectMany(r => r.Report.Advice.Take(3).Select(f => $"{r.Role}, {f.Severity}: {f.Message}")).Take(6).ToList();
            await CommentAsync(request, RevisionReply.Pushed(published.CommitSha, report, comments, BuildFeedback.Summary(results), notes, published.RebasedOver));

            // A courtesy: if it fails the push still stands.
            var reviewers = RevisionComments.Reviewers(reviews);
            if (reviewers.Count > 0)
            {
                await BestEffortAsync(() => Workflow.ExecuteActivityAsync(
                    (ForgejoActivities activities) => activities.RequestReviewersAsync(new ReviewRequestInput(request.Owner, request.Repo, pull.Number, reviewers)),
                    ActivityPolicy.Local(TimeSpan.FromMinutes(1), 2)));
            }
            return new WorkResult(Statuses.RevisionPushed, WorkBranch: published.Branch);
        }

        await CommentAsync(request,
            $"I could not get the changes through the checks and the review in {maxBuildRounds} rounds, so nothing was pushed to this pull request. " +
            $"Last problem: {TicketComments.Inert(Short(feedback ?? "unknown"))} A person needs to take over.");
        return new WorkResult(Statuses.BuildFailed);
    }
}
