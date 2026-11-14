using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public abstract partial class FactoryWorkflowBase
{
    /// <summary>
    /// The stage results this run starts with. A run started by <c>/retry</c> takes over what an earlier run of the same ticket finished, if the ticket and the
    /// default branch are still as they were; anything else (a new ticket, a reopened one, a changed ticket, new commits) starts from nothing, and says why when
    /// a person asked to resume. Reading the saved record never fails the run.
    /// </summary>
    protected async Task<ResumeState> BeginResumeAsync(WorkRequest request, WorkspaceInfo workspace)
    {
        var fresh = ResumeState.Start(request, workspace);
        if (!request.Resume)
        {
            return fresh;
        }

        ResumeState? saved;
        try
        {
            saved = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.LoadResumeAsync(request.WorkflowId),
                ActivityPolicy.Local(TimeSpan.FromMinutes(1), 2));
        }
        catch (ActivityFailureException)
        {
            saved = null;
        }
        if (saved is null || !saved.HasResults)
        {
            await CommentAsync(request, TicketComments.ResumeNothingSaved);
            return fresh;
        }
        if (saved.WhyNot(request, workspace) is { } why)
        {
            await CommentAsync(request, TicketComments.ResumeNotUsable(why));
            return fresh;
        }
        await CommentAsync(request, TicketComments.ResumeUsing(saved.Kept));
        return saved;
    }

    /// <summary>Keeps the stages finished so far for a later <c>/retry</c>. Best effort: a run is never failed because this could not be written.</summary>
    protected static async Task SaveResumeAsync(WorkRequest request, ResumeState state)
    {
        try
        {
            await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.SaveResumeAsync(new SaveResumeRequest(request.WorkflowId, state)),
                ActivityPolicy.Local(TimeSpan.FromMinutes(1), 2));
        }
        catch (ActivityFailureException)
        {
        }
    }

    /// <summary>A pull request was opened, so there is nothing left to resume.</summary>
    protected static async Task ClearResumeAsync(WorkRequest request)
    {
        try
        {
            await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.ClearResumeAsync(request.WorkflowId),
                ActivityPolicy.Local(TimeSpan.FromMinutes(1), 2));
        }
        catch (ActivityFailureException)
        {
        }
    }
}
