using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

/// <summary>
/// A person asked for a review of a pull request: <c>/review</c> in a comment on it, whoever opened it. The project's reviewers (<c>reviewer</c>, and <c>security</c> if the
/// project lists it) read the pull request's diff, as the git host reports it, and post their findings on the pull request. Nothing is written or pushed, and nothing is merged.
/// The checkout is the default branch, so the project's settings and image come from the trusted branch and never from a branch that anybody could have written, and the
/// reviewers read the code the pull request changes as it was, with the diff in the prompt showing what it becomes. The project's checks are not run.
/// </summary>
[Workflow("PullRequestReviewWorkflow")]
public sealed class PullRequestReviewWorkflow : FactoryWorkflowBase
{
    [WorkflowRun]
    public async Task<WorkResult> RunAsync(PullReviewRequest review)
    {
        var placeholder = review.ToWorkRequest($"Pull request #{review.Number}");
        ReviewContext context;
        try
        {
            context = await Workflow.ExecuteActivityAsync(
                (GitHostActivities activities) => activities.ReadReviewContextAsync(review),
                ActivityPolicy.Local(TimeSpan.FromMinutes(2), 3));
        }
        catch (ActivityFailureException error)
        {
            await CommentAsync(placeholder, $"I could not read this pull request, so I did not review it: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))}");
            return new WorkResult(Statuses.Error);
        }

        if (PullReviewRules.Refuse(context) is { } refusal)
        {
            if (refusal.Message is not null)
            {
                await CommentAsync(placeholder, refusal.Message);
            }
            return new WorkResult(refusal.Status);
        }
        if (context.Changes is not { Files.Count: > 0 } changes)
        {
            await CommentAsync(placeholder, TicketComments.PullReviewNoChanges);
            return new WorkResult(Statuses.Error);
        }

        var pull = context.Pull;
        return await RunPipelineAsync(
            review.ToWorkRequest(pull.Title, pull.Body), "pull request review",
            work => StagesAsync(work, pull, changes),
            // A new local branch off the default branch, never pushed: the review reads and writes nothing in the repository.
            workspaceFor: request => new WorkspaceRequest(request.Owner, request.Repo, request.WorkflowId, $"factory/review-{pull.Number}"),
            startMessage: $"I am reviewing pull request #{pull.Number}. I only read it: nothing will be changed.");
    }

    private static string KindOf(string status) => status switch
    {
        "added" => FileChange.Added,
        "deleted" => FileChange.Deleted,
        _ => FileChange.Modified,
    };

    private async Task<WorkResult> StagesAsync(WorkContext work, PullRequestDetails pull, PullChanges changes)
    {
        var (request, _, project) = work;
        // No stage here asks a person a question.
        commentsCanStillBeUsed = false;

        // The pull request stands in for the ticket the reviewers read, and one criterion stands in for the acceptance criteria it may not have.
        IReadOnlyList<CriterionCoverage> criteria =
        [
            new CriterionCoverage(1, "The pull request does what its description says and is safe to merge: no bug, no security problem and no change it does not mention.", CoverageStatus.Missing, [], ""),
        ];
        var changeReport = new ChangeReport(changes.Files.Select(file => new FileChange(file.Path, KindOf(file.Status))).ToList(), []);
        // The prompt carries only the start of a long diff (ReviewPrompt.MaxDiff), and the reviewers are told so.
        var diffCut = changes.DiffCut || changes.Diff.Length > ReviewPrompt.MaxDiff;
        var gateSummary = "The factory did not run the project's checks on this pull request: judge the diff on its own." +
            (diffCut ? " The diff is cut; only its start is shown." : "");
        var checks = ReviewPrompt.Describe(project.Gates);

        var reports = new List<(string Role, ReviewReport Report)>();
        foreach (var role in project.Reviewers)
        {
            var (result, stop) = await RunReviewAsync(work, role,
                problem => ReviewPrompt.Build(request, criteria, null, changeReport, changes.Diff, gateSummary, problem, checks: checks, checksRan: false));
            if (stop is not null)
            {
                return stop.Stop!;
            }
            reports.Add((role, result!));
        }

        await CommentAsync(request, TicketComments.PullReviewDone(pull.Number, reports, diffCut));
        return new WorkResult(Statuses.ReviewPosted);
    }
}
