using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

/// <summary>
/// <c>/setup</c> on a ticket of a repository that has no <c>.factory/project.json</c>: the read-only <c>configurer</c> reads the repository and proposes the settings, and the
/// controller (not the agent) does the rest. It builds the file with the real settings parser, refusing anything the parser refuses and anything that is not one of the five settings
/// a proposal may carry; <b>runs every proposed check</b> in the sandbox (without the model login) to see which really pass, because that is a fact and the agent's word is not;
/// leaves the failing ones out and says so; writes the file itself (agents cannot write under <c>.factory/</c>); and opens a pull request for a person to read. It never picks a
/// repository's own image, never merges, and is the one workflow that may run on a repository without settings.
/// </summary>
[Workflow("SetupWorkflow")]
public sealed class SetupWorkflow : FactoryWorkflowBase
{
    /// <summary>Used only to get a checkout and run the configurer: the standard image that has every toolchain, and no checks.</summary>
    private static readonly ProjectConfig NoSettingsYet = new("full", [], ["reviewer"]);

    [WorkflowRun]
    public Task<WorkResult> RunAsync(WorkRequest request) =>
        RunPipelineAsync(request, "setup", StagesAsync,
            workspaceFor: r => new WorkspaceRequest(r.Owner, r.Repo, r.WorkflowId, $"factory/setup-{r.IssueNumber}"),
            startMessage: SetupComments.Started, whenConfigMissing: NoSettingsYet);

    private async Task<WorkResult> StagesAsync(WorkContext work)
    {
        var (request, workspace, project) = work;
        // No stage here asks a person a question: the pull request is where a person corrects the proposal.
        commentsCanStillBeUsed = false;
        if (!projectWasMissing)
        {
            await CommentAsync(request, SetupComments.AlreadyConfigured);
            return new WorkResult(Statuses.SetupNotNeeded);
        }

        var outcome = await RunAgentAsync<SetupProposal>(
            "configurer", project.Image, workspace,
            problem => SetupPrompts.Build(request, problem),
            text => SetupParser.TryParse(text, out var value, out var error) ? (value, null) : (null, error));
        if (outcome.NotSignedIn)
        {
            await CommentAsync(request, NotSignedInMessage);
            return new WorkResult(Statuses.Blocked);
        }
        if (outcome.Value is not { } proposal)
        {
            await CommentAsync(request, $"I could not propose settings: {outcome.Describe()}");
            return new WorkResult(outcome.FailedStatus(Statuses.ConfigureFailed));
        }
        if (proposal.Verdict == SetupProposal.CannotConfigure || proposal.Project is not { } proposed)
        {
            await CommentAsync(request, SetupComments.Declined(proposal));
            return new WorkResult(Statuses.SetupDeclined);
        }

        // The facts: every proposed check, run in the proposed image, one at a time.
        var results = new List<GateResult>();
        foreach (var gate in proposed.Gates)
        {
            var result = await RunGateAsync(proposed.Image, workspace, gate);
            if (result.Infrastructure)
            {
                await CommentAsync(request, $"A proposed check could not run: {result.Output}{TicketComments.RetryHint}");
                return new WorkResult(Statuses.InfrastructureFailed);
            }
            results.Add(result);
        }

        var failed = results.Where(r => !r.Passed).Select(r => r.Name).ToHashSet();
        if (proposal.FileWithout(failed) is not { } file)
        {
            await CommentAsync(request, SetupComments.NoCheckPassed(results));
            return new WorkResult(Statuses.SetupDeclined);
        }

        PublishResult? published = null;
        try
        {
            published = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.PublishSettingsAsync(new PublishSettingsRequest(
                    workspace.Name, file, $"Add .factory/project.json (#{request.IssueNumber})\n\nProposed by the factory; every check in it passed in the sandbox.")),
                ActivityPolicy.Local(TimeSpan.FromMinutes(5), 3));
            var pullRequest = await Workflow.ExecuteActivityAsync(
                (ForgejoActivities activities) => activities.CreatePullRequestAsync(new PullRequestRequest(
                    request.Owner, request.Repo, published.Branch, workspace.DefaultBranch, "Add .factory/project.json",
                    SetupComments.PullRequestBody(request.IssueNumber, proposal, results, file))),
                ActivityPolicy.Local(TimeSpan.FromSeconds(60), 3));
            await CommentAsync(request, SetupComments.PullRequestOpened(pullRequest.Url, published.Branch, results, results.Count - failed.Count));
            return new WorkResult(Statuses.PrOpened, PullRequestUrl: pullRequest.Url, WorkBranch: published.Branch);
        }
        catch (ActivityFailureException error)
        {
            var reason = TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message));
            await CommentAsync(request, published is not null
                ? $"The settings are on the branch `{published.Branch}`, but opening the pull request failed: {reason} Open one from that branch by hand."
                : $"Publishing the settings failed and needs a person to look at it: {reason}");
            return new WorkResult(Statuses.PublishFailed, WorkBranch: published?.Branch);
        }
    }
}
