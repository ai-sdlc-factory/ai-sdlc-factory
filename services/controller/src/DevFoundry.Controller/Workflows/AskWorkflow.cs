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
        if (ask.Question.Length == 0)
        {
            await CommentAsync(ask.ToWorkRequest(), AskComments.Usage);
            return new WorkResult("ignored");
        }

        var pull = ask.OnPullRequest ? await ReadPullContextAsync(ask) : null;
        if (pull is not null)
        {
            ask = ask with { Title = pull.Title, Body = pull.Body };
        }

        return await RunPipelineAsync(
            ask.ToWorkRequest(), "question",
            work => StagesAsync(work, ask, pull),
            // A throwaway local branch off the default branch, never pushed: the agent reads and nothing is written.
            workspaceFor: r => new WorkspaceRequest(r.Owner, r.Repo, r.WorkflowId, $"factory/ask-{ask.Number}"),
            startMessage: AskComments.Started(ask.OnPullRequest));
    }

    /// <summary>What a question on a pull request is told about it. The changed files and the diff are null when Forgejo could not give them.</summary>
    private sealed record PullContext(string Title, string Body, string? ChangedFiles, string? Diff);

    private const int MaxFilesListed = 60;

    /// <summary>The pull request's description and what it changes, or null when Forgejo cannot be read: the question can still be answered from the repository itself.</summary>
    private static async Task<PullContext?> ReadPullContextAsync(AskRequest ask)
    {
        try
        {
            var context = await Workflow.ExecuteActivityAsync(
                (ForgejoActivities activities) => activities.ReadReviewContextAsync(new PullReviewRequest(ask.Owner, ask.Repo, ask.Number, ask.Author)),
                ActivityPolicy.Local(TimeSpan.FromMinutes(2), 3));
            var changed = context.Changes is { } changes
                ? string.Join('\n', changes.Files.Take(MaxFilesListed).Select(file => $"{file.Status} {file.Path} (+{file.Additions} -{file.Deletions})"))
                : null;
            return new PullContext(context.Pull.Title, context.Pull.Body, changed, context.Changes?.Diff);
        }
        catch (ActivityFailureException)
        {
            return null;
        }
    }

    private async Task<WorkResult> StagesAsync(WorkContext work, AskRequest ask, PullContext? pull)
    {
        var (request, workspace, project) = work;
        // No stage here asks a person a question.
        commentsCanStillBeUsed = false;

        var outcome = await RunAgentAsync<AskAnswer>(
            "asker", project.Image, workspace,
            problem => AskPrompts.Build(ask, pull?.ChangedFiles, pull?.Diff, problem),
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
