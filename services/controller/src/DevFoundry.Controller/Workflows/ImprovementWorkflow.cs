using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

/// <summary>
/// The weekly evaluation. The report on what repeated (rejected answers, rounds used, runs that stopped the same way, token outliers, refused hosts) is made by counting the
/// host's logs, with no model, and saved on the host. If a repository is set for the factory's own tickets and the report found something, the report is opened there as a ticket
/// (no factory label, so nothing starts on it), and the read-only <c>improver</c> agent reads the factory's repository and proposes changes to its prompts and settings as a comment
/// on that ticket. The agent cannot change anything, the proposals are checked only for naming files that exist, and nothing is ever applied by the factory: a person reads them.
/// </summary>
[Workflow("ImprovementWorkflow")]
public sealed class ImprovementWorkflow : FactoryWorkflowBase
{
    [WorkflowRun]
    public async Task<WorkResult> RunAsync(ImprovementRequest request)
    {
        // What became of the pull requests is asked of Forgejo in an activity (never from workflow code), and read-only. If it cannot be known the report says so and goes on.
        var outcomes = await ReadPullOutcomesAsync(request);
        ImprovementReportResult report;
        try
        {
            report = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.BuildImprovementReportAsync(request, outcomes),
                ActivityPolicy.Local(TimeSpan.FromMinutes(5), 2));
        }
        catch (ActivityFailureException)
        {
            return new WorkResult(Statuses.ImprovementFailed);
        }

        // Nothing to say, or nowhere to say it: the report is on the host and that is all.
        if (request.Repository is not { Length: > 0 } repository || !report.HasFindings || repository.Split('/') is not [var owner, var repo])
        {
            return new WorkResult(Statuses.ReportSaved);
        }

        var title = $"Factory report, {request.Week}";
        IssueInfo issue;
        try
        {
            issue = await Workflow.ExecuteActivityAsync(
                (ForgejoActivities activities) => activities.CreateIssueAsync(new CreateIssueRequest(owner, repo, title, report.Markdown, new List<string>())),
                // One attempt: a second after a lost answer would open the same report twice.
                ActivityPolicy.Local(TimeSpan.FromSeconds(60), 1));
        }
        catch (ActivityFailureException)
        {
            return new WorkResult(Statuses.ImprovementFailed);
        }

        var ticket = new WorkRequest(owner, repo, issue.Number, title, report.Markdown, "factory", new List<string>(), WorkIds.Improvement);
        return await RunPipelineAsync(
            ticket, "improvement",
            work => StagesAsync(work, request.Week),
            // A throwaway local branch off the default branch, never pushed: the agent reads and nothing is written.
            workspaceFor: r => new WorkspaceRequest(r.Owner, r.Repo, r.WorkflowId, $"factory/improvement-{request.Week.ToLowerInvariant()}"),
            startMessage: "I am reading this report to propose changes. I only read: nothing will be changed or applied.");
    }

    /// <summary>The state of the factory's pull requests of the week, or null when it could not be read (the report then says so instead of showing nothing).</summary>
    private static async Task<IReadOnlyList<PullOutcome>?> ReadPullOutcomesAsync(ImprovementRequest request)
    {
        try
        {
            var scope = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.ReadPullScopeAsync(request),
                ActivityPolicy.Local(TimeSpan.FromMinutes(2), 2));
            if (scope.Repositories.Count == 0)
            {
                return [];
            }
            return await Workflow.ExecuteActivityAsync(
                (ForgejoActivities activities) => activities.ReadPullOutcomesAsync(new PullOutcomeRequest(
                    scope,
                    DateTimeOffset.ParseExact(request.From, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal),
                    DateTimeOffset.ParseExact(request.To, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal))),
                ActivityPolicy.Local(TimeSpan.FromMinutes(5), 2));
        }
        catch (ActivityFailureException)
        {
            return null;
        }
    }

    private async Task<WorkResult> StagesAsync(WorkContext work, string week)
    {
        var (request, workspace, project) = work;
        // No stage here asks a person a question.
        commentsCanStillBeUsed = false;

        string? feedback = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var feedbackNow = feedback;
            var outcome = await RunAgentAsync<ImprovementProposals>(
                "improver", project.Image, workspace,
                problem => ImprovementPrompts.Build(request.Body, feedbackNow, problem),
                text => ImprovementParser.TryParse(text, out var value, out var error) ? (value, null) : (null, error));
            if (outcome.NotSignedIn)
            {
                await CommentAsync(request, NotSignedInMessage);
                return new WorkResult(Statuses.Blocked);
            }
            if (outcome.Value is not { } answer)
            {
                await CommentAsync(request, $"The proposals could not be made: {outcome.Describe()}");
                return new WorkResult(outcome.FailedStatus(Statuses.ImprovementFailed));
            }
            if (answer.Verdict == ImprovementProposals.Nothing)
            {
                await CommentAsync(request, ImprovementComments.NothingToPropose(week, answer.Summary));
                return new WorkResult(Statuses.ImprovementPosted);
            }

            // The only thing the factory checks: every proposal names a file that is really there, so none points at something the agent made up.
            var existing = await FindExistingFilesAsync(workspace, project.Image, answer.Proposals.Select(proposal => proposal.Target).Distinct().ToList());
            var kept = answer.Proposals.Where(proposal => existing.Contains(proposal.Target)).ToList();
            if (kept.Count == 0)
            {
                feedback = $"None of the files you named exist in the repository: {string.Join(", ", answer.Proposals.Select(p => p.Target).Distinct().Take(8))}. Read the repository and name files that exist, or answer nothing.";
                if (attempt < 2)
                {
                    continue;
                }
                await CommentAsync(request, "The proposals named no file that exists in the repository, so none was posted. The report above stands on its own.");
                return new WorkResult(Statuses.ImprovementFailed);
            }
            await CommentAsync(request, ImprovementComments.Proposals(week, answer, kept, answer.Proposals.Count - kept.Count));
            return new WorkResult(Statuses.ImprovementPosted);
        }
        return new WorkResult(Statuses.ImprovementFailed);
    }
}
