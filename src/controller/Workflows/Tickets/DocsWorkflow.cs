using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AiSdlcFactory.Controller;

/// <summary>
/// One run per ticket labelled <c>docs</c>: the documentation writer writes the repository's AGENTS.md (or brings it up to date) and the documents next to it, from the
/// code alone, guided by what the ticket says. The factory allows changes only to AGENTS.md, README.md, CONTRIBUTING.md and Markdown under docs/, runs the reviewers on
/// the result (is every statement true of the code?), and opens a pull request that a person must read before merging. The project's code checks are not run: no code changed.
/// </summary>
[Workflow("DocsWorkflow")]
public sealed class DocsWorkflow : FactoryWorkflowBase
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
    public Task<WorkResult> RunAsync(WorkRequest request) => RunPipelineAsync(request, "documentation", StagesAsync);

    private async Task<WorkResult> StagesAsync(WorkContext work)
    {
        var (request, workspace, project) = work;
        // No stage here asks a person a question: the ticket says what to document, and the pull request is where a person corrects it.
        commentsCanStillBeUsed = false;

        IReadOnlyList<CriterionCoverage> criteria =
        [
            new CriterionCoverage(1, "Every statement in the changed documents is true of the code in this repository, and nothing in them is invented.", CoverageStatus.Missing, [], ""),
        ];

        string? feedback = null;
        for (var round = 1; round <= maxBuildRounds; round++)
        {
            if (ticketClosed)
            {
                return new WorkResult(Statuses.Closed);
            }
            NoteRound(round);
            var feedbackForRound = feedback;
            var outcome = await RunAgentAsync<DocsReport>(
                "docs-writer", project.Image, workspace,
                problem => DocsPrompts.Build(request, feedbackForRound, round, maxBuildRounds, problem),
                text => DocsParser.TryParse(text, out var value, out var error) ? (value, null) : (null, error),
                writable: true, timeoutSeconds: DeveloperTimeoutSeconds);
            if (ticketClosed)
            {
                return new WorkResult(Statuses.Closed);
            }
            if (outcome.NotSignedIn)
            {
                await CommentAsync(request, NotSignedInMessage);
                return new WorkResult(Statuses.Blocked);
            }
            if (outcome.Value is not { } report)
            {
                await CommentAsync(request, $"The documentation could not be written and a person needs to look at it: {outcome.Describe()}");
                return new WorkResult(outcome.FailedStatus(Statuses.BuildFailed));
            }
            if (report.Verdict == DocsReport.Blocked)
            {
                await CommentAsync(request, $"**Documentation: I cannot continue**\n\n{TicketComments.Inert(report.Summary)}" +
                    string.Concat(report.Notes.Select(note => $"\n- {TicketComments.Inert(note)}")));
                return new WorkResult(Statuses.BuildBlocked);
            }

            var changes = await DetectChangesAsync(workspace);
            if (changes.Violations.Count > 0)
            {
                feedback = BuildFeedback.FromViolations(changes.Violations);
                await CommentAsync(request, $"Documentation round {round}: the changes were rejected: {TicketComments.Inert(Short(feedback))}");
                continue;
            }
            if (changes.Files.Count == 0)
            {
                if (report.Verdict == DocsReport.Unchanged)
                {
                    await CommentAsync(request, $"**Documentation: nothing to change**\n\n{TicketComments.Inert(report.Summary)}\n\nThe documents already say what the repository does, so I opened no pull request.");
                    return new WorkResult(Statuses.NoChanges);
                }
                feedback = "You answered done, but no file in the workspace changed. Write or change the documents, or answer unchanged if they are already right.";
                await CommentAsync(request, $"Documentation round {round}: {TicketComments.Inert(Short(feedback))}");
                continue;
            }
            if (DocsFiles.Problem(changes.Files) is { } notAllowed)
            {
                feedback = notAllowed;
                await CommentAsync(request, $"Documentation round {round}: the changes were rejected: {TicketComments.Inert(Short(feedback))}");
                continue;
            }

            await CommentAsync(request, $"Documentation round {round}: {changes.Files.Count} file{(changes.Files.Count == 1 ? "" : "s")} written. Asking the reviewers to check it against the code.");
            var reviewRequest = request with
            {
                Title = $"Documentation for {request.Owner}/{request.Repo}: {request.Title}",
                Body = "Documents were written from the code. Check that each statement in them is true of the code, and that nothing is invented.",
            };
            var review = await ReviewAsync(work with { Request = reviewRequest }, criteria, null, changes, [], new ReviewOptions(SkipQa: true));
            if (review.Stop is { } reviewStop)
            {
                return reviewStop;
            }
            if (review.Feedback is not null)
            {
                feedback = review.Feedback;
                continue;
            }

            var title = $"Docs: {request.Title}";
            var prTitle = title.Length > 120 ? title[..120] : title;
            var notes = review.Reports.SelectMany(r => r.Report.Advice.Take(3).Select(f => $"{r.Role}, {f.Severity}: {f.Message}")).Take(6).ToList();
            PublishResult? published = null;
            try
            {
                published = await Workflow.ExecuteActivityAsync(
                    (WorkspaceActivities activities) => activities.PublishAsync(new PublishRequest(
                        workspace.Name,
                        $"{prTitle} (#{request.IssueNumber})\n\nDocumentation written by the factory from the code.\n\n{TicketComments.Inert(report.Summary)}",
                        RecordSpec(request, $"Published as a documentation pull request (round {round})."))),
                    ActivityPolicy.Local(TimeSpan.FromMinutes(5), 3));
                if (published is null)
                {
                    await CommentAsync(request, "The work produced no changes to publish.");
                    return new WorkResult(Statuses.NoChanges);
                }
                var body = $"Refs #{request.IssueNumber}\n\n**Documentation written by the factory from the code.** It has been read by the reviewers, who checked it against the code, " +
                    "but nobody has run anything. A person must read it and correct whatever is wrong before merging.\n\n" +
                    $"{TicketComments.Inert(report.Summary)}\n\nFiles: {string.Join(", ", changes.Files.Select(file => $"`{file.Path}`"))}" +
                    string.Concat(report.Notes.Concat(notes).Select(note => $"\n- {TicketComments.Inert(note)}"));
                var pullRequest = await Workflow.ExecuteActivityAsync(
                    (GitHostActivities activities) => activities.CreatePullRequestAsync(new PullRequestRequest(
                        request.Owner, request.Repo, published.Branch, workspace.DefaultBranch, prTitle, body)),
                    ActivityPolicy.Local(TimeSpan.FromSeconds(60), 3));
                await CommentAsync(request, $"**Pull request opened:** {pullRequest.Url}\n\n{changes.Files.Count} documentation file{(changes.Files.Count == 1 ? "" : "s")} on branch `{published.Branch}`. A person must read and merge it.");
                return new WorkResult(Statuses.PrOpened, PullRequestUrl: pullRequest.Url, WorkBranch: published.Branch);
            }
            catch (ActivityFailureException error)
            {
                var reason = TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message));
                await CommentAsync(request, published is not null
                    ? $"The documentation passed the review and is on the branch `{published.Branch}`, but opening the pull request failed: {reason} Open one from that branch by hand."
                    : $"Publishing the documentation failed and needs a person to look at it: {reason}");
                return new WorkResult(Statuses.PublishFailed, WorkBranch: published?.Branch);
            }
        }

        await CommentAsync(request,
            $"I could not get the documentation through the checks and the review in {maxBuildRounds} rounds, so nothing was published. " +
            $"Last problem: {TicketComments.Inert(Short(feedback ?? "unknown"))} A person needs to take over.");
        return new WorkResult(Statuses.BuildFailed);
    }
}
