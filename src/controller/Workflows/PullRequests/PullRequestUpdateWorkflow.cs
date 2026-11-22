using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AiSdlcFactory.Controller;

/// <summary>
/// A person asked for the factory's pull request to be brought up to date: <c>/update</c> in a comment on the ticket or on the pull request. The
/// controller merges the base branch into the pull request's branch in its own trusted clone. A merge that git does by itself is checked with the
/// project's checks; a merge with conflicts goes to the merge resolver first, which resolves them in a copy of the merged files. Either way the
/// factory then runs the same change checks, project checks and reviewers as for a revision, and pushes one merge commit onto the same branch
/// (never forcing, so a person's work is never overwritten). An update does not count as a revision.
/// </summary>
[Workflow("PullRequestUpdateWorkflow")]
public sealed partial class PullRequestUpdateWorkflow : FactoryWorkflowBase
{
    [WorkflowRun]
    public async Task<WorkResult> RunAsync(UpdateRequest update)
    {
        var placeholder = update.ToWorkRequest($"Pull request #{update.Number}");
        UpdateContext context;
        try
        {
            context = await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.ReadUpdateContextAsync(update),
                ActivityPolicy.Local(TimeSpan.FromMinutes(2), 3));
        }
        catch (ActivityFailureException error)
        {
            await CommentAsync(placeholder, $"I could not read the pull request, so I did not update it: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))}");
            return new WorkResult(Statuses.Error);
        }

        if (UpdateRules.Refuse(update, context) is { } refusal)
        {
            if (refusal.Message is not null)
            {
                await CommentAsync(placeholder, refusal.Message);
            }
            return new WorkResult(refusal.Status);
        }

        var pull = context.Pull!;
        return await RunPipelineAsync(
            update.ToWorkRequest(pull.Title), "pull request update",
            work => StagesAsync(work, pull),
            workspaceFor: request => new WorkspaceRequest(request.Owner, request.Repo, request.WorkflowId, pull.HeadRef, ExistingBranch: true),
            startMessage: UpdateReply.Starting(pull.BaseRef, pull.Number));
    }

    private static (MergeReport? Value, string? Error) ParseMerge(string text, IReadOnlyList<string> conflicted) =>
        MergeParser.TryParse(text, conflicted, out var report, out var error) ? (report, null) : (null, error);

    private async Task<WorkResult> StagesAsync(WorkContext work, PullRequestDetails pull)
    {
        var (request, workspace, project) = work;
        // No stage here asks a person a question: what cannot be done is said on the ticket instead.
        commentsCanStillBeUsed = false;
        var baseBranch = pull.BaseRef;

        MergeResult merge;
        try
        {
            merge = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.MergeBaseAsync(new MergeBaseRequest(workspace.Name, baseBranch)),
                ActivityPolicy.Local(TimeSpan.FromMinutes(10), 2));
        }
        catch (ActivityFailureException error)
        {
            await CommentAsync(request, UpdateReply.MergeFailed(baseBranch, Short(error.InnerException?.Message ?? error.Message)));
            return new WorkResult(Statuses.UpdateBlocked);
        }

        if (merge.Outcome == MergeResult.UpToDate)
        {
            await CommentAsync(request, UpdateReply.UpToDate(baseBranch));
            return new WorkResult(Statuses.UpToDate);
        }

        var conflicts = merge.Conflicts;
        if (conflicts.Count > GitWorkspace.MaxConflictedFiles)
        {
            await CommentAsync(request, UpdateReply.TooManyConflicts(baseBranch, conflicts.Count));
            return new WorkResult(Statuses.UpdateBlocked);
        }
        var extraProtected = (project.ProtectedPaths ?? []).ToList();
        if (project.Verify is { } verify)
        {
            extraProtected.Add(verify.ProtectedPattern);
        }
        var protectedConflicts = conflicts.Where(conflict => ProtectedPaths.IsProtected(conflict.Path, extraProtected)).Select(conflict => conflict.Path).ToList();
        if (protectedConflicts.Count > 0)
        {
            await CommentAsync(request, UpdateReply.ProtectedConflicts(baseBranch, protectedConflicts));
            return new WorkResult(Statuses.UpdateBlocked);
        }
        await CommentAsync(request, conflicts.Count > 0 ? UpdateReply.Conflicted(baseBranch, conflicts) : UpdateReply.Merged(baseBranch, merge));

        // The reviewers are told what a good merge is, and the one criterion they check.
        var reviewRequest = request with
        {
            Title = $"Merge of {baseBranch} into pull request #{pull.Number}: {pull.Title}",
            Body = $"The base branch {baseBranch} was merged into this pull request's branch. {conflicts.Count} file(s) were in conflict. Review the resolution.",
        };
        IReadOnlyList<CriterionCoverage> criteria =
        [
            new CriterionCoverage(1, $"The merge keeps both the pull request's change and what {baseBranch} changed: nothing of either is lost, no conflict marker is left, and the code still fits together.", CoverageStatus.Missing, [], ""),
        ];

        var conflictedPaths = conflicts.Select(conflict => conflict.Path).ToList();
        // Git did the merge by itself: the agent is asked only when the checks then fail.
        var needsAgent = conflicts.Count > 0;
        MergeReport? report = null;
        string? feedback = null;
        string? earlierFindings = null; // what the last review asked for, so the next review checks it was done
        for (var round = 1; round <= maxBuildRounds; round++)
        {
            NoteRound(round);
            if (needsAgent)
            {
                var feedbackForRound = feedback;
                var roundNow = round;
                var outcome = await RunAgentAsync<MergeReport>(
                    "merge-resolver", project.Image, workspace,
                    problem => UpdatePrompts.Build(pull, baseBranch, merge, conflicts, project.Gates, Combine(feedbackForRound, problem), roundNow, maxBuildRounds, project.Rules),
                    text => ParseMerge(text, conflictedPaths), writable: true, timeoutSeconds: DeveloperTimeoutSeconds);
                if (outcome.NotSignedIn)
                {
                    await CommentAsync(request, NotSignedInMessage);
                    return new WorkResult(Statuses.Blocked);
                }
                if (outcome.Value is not { } answer)
                {
                    await CommentAsync(request, $"I could not resolve the merge and a person needs to look at it: {outcome.Describe()}");
                    return new WorkResult(outcome.FailedStatus(Statuses.BuildFailed));
                }
                if (answer.Verdict == MergeReport.Blocked)
                {
                    await CommentAsync(request, $"**Update: I cannot continue**\n\n{TicketComments.Inert(answer.Summary)}" +
                        string.Concat(answer.Notes.Select(note => $"\n- {TicketComments.Inert(note)}")) +
                        "\n\nNothing was pushed. A person has to merge this one.");
                    return new WorkResult(Statuses.UpdateBlocked);
                }
                report = answer;

                var before = await DetectChangesAsync(workspace);
                if (before.Violations.Count > 0)
                {
                    feedback = BuildFeedback.FromViolations(before.Violations);
                    await CommentAsync(request, $"Update round {round}: the changes were rejected: {TicketComments.Inert(Short(feedback))}");
                    continue;
                }
                var markers = await Workflow.ExecuteActivityAsync(
                    (WorkspaceActivities activities) => activities.FindConflictMarkersAsync(workspace.Name),
                    ActivityPolicy.Local(TimeSpan.FromMinutes(2), 2));
                if (markers.Count > 0)
                {
                    feedback = $"Conflict markers (<<<<<<< or >>>>>>>) are still in {string.Join(", ", markers.Take(10))}. Resolve those conflicts completely.";
                    await CommentAsync(request, $"Update round {round}: {TicketComments.Inert(Short(feedback))}");
                    continue;
                }
            }

            var verified = await VerifyAsync(work, $"Update round {round}");
            if (verified.Stop is { } infrastructureStop)
            {
                return infrastructureStop;
            }
            feedback = verified.Feedback;
            if (feedback is not null)
            {
                needsAgent = true;
                continue;
            }
            var (results, after, ruleNotes) = (verified.Results, verified.After, verified.RuleNotes);

            // What the factory itself merged needs no review; what an agent wrote does.
            IReadOnlyList<string> notes = [];
            if (report is not null && after.Files.Count > 0)
            {
                await CommentAsync(request, $"Update round {round}: {BuildFeedback.Summary(results)}. Asking the reviewers.");
                var review = await ReviewAsync(work with { Request = reviewRequest }, criteria, null, after, results, new ReviewOptions(SkipQa: true, RuleNotes: ruleNotes, EarlierFindings: earlierFindings));
                if (review.Stop is { } reviewStop)
                {
                    return reviewStop;
                }
                if (review.Feedback is not null)
                {
                    feedback = review.Feedback;
                    earlierFindings = review.Feedback;
                    needsAgent = true;
                    continue;
                }
                notes = review.Reports.SelectMany(r => r.Report.Advice.Take(3).Select(f => $"{r.Role}, {f.Severity}: {f.Message}")).Take(6).ToList();
            }

            var message = $"Merge branch '{baseBranch}' into {pull.HeadRef}\n\nBrought up to date by the factory for pull request #{pull.Number}." +
                (report is null ? "" : $"\n\n{TicketComments.Inert(report.Summary)}");
            PublishResult published;
            try
            {
                published = await Workflow.ExecuteActivityAsync(
                    (WorkspaceActivities activities) => activities.PublishMergeAsync(new PublishMergeRequest(
                        workspace.Name, message, RecordSpec(request, $"Pushed as an update of pull request #{pull.Number} (round {round})."))),
                    ActivityPolicy.Local(TimeSpan.FromMinutes(5), 3));
            }
            catch (ActivityFailureException error)
            {
                await CommentAsync(request, $"The merge passed every check, but pushing it failed: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))} Comment `/update` again once the branch is stable.");
                return new WorkResult(Statuses.PublishFailed);
            }

            await CommentAsync(request, UpdateReply.Pushed(published.CommitSha, baseBranch, merge, report, BuildFeedback.Summary(results), notes));
            return new WorkResult(Statuses.UpdatePushed, WorkBranch: published.Branch);
        }

        await CommentAsync(request,
            $"I could not get the merge through the checks and the review in {maxBuildRounds} rounds, so nothing was pushed to this pull request. " +
            $"Last problem: {TicketComments.Inert(Short(feedback ?? "unknown"))} A person needs to take over.");
        return new WorkResult(Statuses.BuildFailed);
    }
}
