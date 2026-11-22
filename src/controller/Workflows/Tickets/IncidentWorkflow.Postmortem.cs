using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AiSdlcFactory.Controller;

public sealed partial class IncidentWorkflow
{
    /// <summary>
    /// Drafts a postmortem as a new document in the repository and opens a pull request for it. The factory checks that
    /// exactly one new file, at the expected path, was written; the draft comes from the ticket's record only.
    /// </summary>
    private async Task<WorkResult> PostmortemAsync(
        WorkContext work, IncidentAssessment assessment, Investigation? investigation, Mitigation? mitigation)
    {
        var (request, workspace, project) = work;
        var path = $"docs/postmortems/{Workflow.UtcNow:yyyy-MM-dd}-incident-{request.IssueNumber}.md";
        string? feedback = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var feedbackForAttempt = feedback;
            var outcome = await RunAgentAsync<PostmortemDraft>(
                "postmortem-writer", project.Image, workspace,
                problem => IncidentPrompts.Postmortem(request, assessment, investigation, mitigation, timeline, path, Combine(feedbackForAttempt, problem)),
                text => IncidentParser.TryParsePostmortem(text, out var value, out var error) ? (value, null) : (null, error),
                writable: true);
            if (ticketClosed)
            {
                return new WorkResult(Statuses.Closed);
            }
            if (outcome.NotSignedIn)
            {
                await CommentAsync(request, NotSignedInMessage);
                return new WorkResult(Statuses.Blocked);
            }
            if (outcome.Value is not { } draft)
            {
                await CommentAsync(request, $"The postmortem draft failed: {outcome.Describe()}");
                return new WorkResult(outcome.FailedStatus(Statuses.PostmortemFailed));
            }
            if (draft.Verdict == PostmortemDraft.Blocked)
            {
                await CommentAsync(request, $"I could not write the postmortem: {TicketComments.Inert(Short(draft.Summary))}");
                return new WorkResult(Statuses.PostmortemFailed);
            }

            var changes = await DetectChangesAsync(workspace);
            if (changes.Violations.Count > 0)
            {
                feedback = BuildFeedback.FromViolations(changes.Violations);
            }
            else if (changes.Files.Count != 1 || changes.Files[0].Path != path || changes.Files[0].Kind != FileChange.Added)
            {
                feedback = $"Write exactly one new file, {path}, and change nothing else. The workspace has: {string.Join(", ", changes.Files.Select(f => $"{f.Kind} {f.Path}").DefaultIfEmpty("no changes"))}.";
            }
            if (feedback is not null && attempt < 2)
            {
                await CommentAsync(request, $"The postmortem draft was not accepted: {TicketComments.Inert(Short(feedback))}");
                continue;
            }
            if (changes.Violations.Count > 0 || changes.Files.Count != 1 || changes.Files[0].Path != path || changes.Files[0].Kind != FileChange.Added)
            {
                await CommentAsync(request, $"The postmortem draft was not accepted and nothing was published: {TicketComments.Inert(Short(feedback ?? "unknown problem"))}");
                return new WorkResult(Statuses.PostmortemFailed);
            }

            try
            {
                var title = $"Postmortem: {PullRequestText.Title(request)}";
                var prTitle = title.Length > 120 ? title[..120] : title;
                var prBody = $"Refs #{request.IssueNumber}\n\n**Draft postmortem.** Written by the factory from the incident ticket's record only: the comments, " +
                    "the assessment, the hypotheses and the options. It has not been checked against logs or metrics. A person must verify the facts, " +
                    "fill in what is marked unknown, and agree the action items before merging.\n\n" +
                    $"Severity: {assessment.Severity}. {TicketComments.Inert(draft.Summary)}";
                var published = await Workflow.ExecuteActivityAsync(
                    (WorkspaceActivities activities) => activities.PublishAsync(new PublishRequest(
                        workspace.Name,
                        $"{title} (#{request.IssueNumber})\n\nDraft postmortem written by the factory from the ticket record.",
                        RecordSpec(request, "Published as a draft postmortem pull request."))),
                    ActivityPolicy.Local(TimeSpan.FromMinutes(5), 3));
                if (published is null)
                {
                    await CommentAsync(request, "The postmortem produced no changes to publish.");
                    return new WorkResult(Statuses.PostmortemFailed);
                }
                var pullRequest = await Workflow.ExecuteActivityAsync(
                    (GitHostActivities activities) => activities.CreatePullRequestAsync(new PullRequestRequest(
                        request.Owner, request.Repo, published.Branch, workspace.DefaultBranch, prTitle, prBody)),
                    ActivityPolicy.Local(TimeSpan.FromSeconds(60), 3));
                await CommentAsync(request, IncidentComments.PostmortemOpened(pullRequest.Url));
                return new WorkResult(Statuses.PostmortemOpened, PullRequestUrl: pullRequest.Url, WorkBranch: published.Branch);
            }
            catch (ActivityFailureException error)
            {
                await CommentAsync(request, $"Publishing the postmortem failed: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))}");
                return new WorkResult(Statuses.PostmortemFailed);
            }
        }
        return new WorkResult(Statuses.PostmortemFailed);
    }
}
