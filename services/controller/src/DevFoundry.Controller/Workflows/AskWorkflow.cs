using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

/// <summary>
/// A person asked a question with <c>/ask</c> in a comment on a ticket or a pull request. The read-only <c>asker</c> reads the default branch (for a pull request it is also given
/// what the pull request changes, as Forgejo reports it) and its answer is posted as a comment. Nothing is written, pushed or run, and the agent has no shell. The files the answer
/// names are checked to exist before they are shown, so a made-up path is not presented as a source. The question is data: it cannot change what the agent may do.
/// </summary>
[Workflow("AskWorkflow")]
public sealed class AskWorkflow : FactoryWorkflowBase
{
    [WorkflowRun]
    public async Task<WorkResult> RunAsync(AskRequest ask)
    {
        var request = ask.ToWorkRequest();
        if (ask.Question.Length == 0)
        {
            await CommentAsync(request, AskComments.Usage);
            return new WorkResult("ignored");
        }

        string? changedFiles = null;
        string? diff = null;
        if (ask.OnPullRequest)
        {
            try
            {
                var context = await Workflow.ExecuteActivityAsync(
                    (ForgejoActivities activities) => activities.ReadReviewContextAsync(new PullReviewRequest(ask.Owner, ask.Repo, ask.Number, ask.Author)),
                    ActivityPolicy.Local(TimeSpan.FromMinutes(2), 3));
                request = request with { Title = context.Pull.Title, Body = context.Pull.Body };
                if (context.Changes is { } changes)
                {
                    changedFiles = string.Join('\n', changes.Files.Take(60).Select(file => $"{file.Status} {file.Path} (+{file.Additions} -{file.Deletions})"));
                    diff = changes.Diff;
                }
            }
            catch (ActivityFailureException)
            {
                // The question can still be answered from the repository itself.
            }
        }

        return await RunPipelineAsync(
            request, "question",
            work => StagesAsync(work, ask with { Title = request.Title, Body = request.Body }, changedFiles, diff),
            // A throwaway local branch off the default branch, never pushed: the agent reads and nothing is written.
            workspaceFor: r => new WorkspaceRequest(r.Owner, r.Repo, r.WorkflowId, $"factory/ask-{ask.Number}"),
            startMessage: AskComments.Started(ask.OnPullRequest));
    }

    private async Task<WorkResult> StagesAsync(WorkContext work, AskRequest ask, string? changedFiles, string? diff)
    {
        var (request, workspace, project) = work;
        // No stage here asks a person a question.
        commentsCanStillBeUsed = false;

        var outcome = await RunAgentAsync<AskAnswer>(
            "asker", project.Image, workspace,
            problem => AskPrompts.Build(ask, changedFiles, diff, problem),
            text => AskParser.TryParse(text, out var value, out var error) ? (value, null) : (null, error));
        if (outcome.NotSignedIn)
        {
            await CommentAsync(request, NotSignedInMessage);
            return new WorkResult(Statuses.Blocked);
        }
        if (outcome.Value is not { } answer)
        {
            await CommentAsync(request, $"I could not answer: {outcome.Describe()}");
            return new WorkResult(outcome.FailedStatus(Statuses.AskFailed));
        }

        var existing = await FindExistingFilesAsync(workspace, project.Image, answer.Files);
        var read = answer.Files.Where(existing.Contains).ToList();
        await CommentAsync(request, AskComments.Answer(answer, read));
        return new WorkResult(answer.Verdict == AskAnswer.Answered ? Statuses.Answered : Statuses.NoAnswer);
    }
}
