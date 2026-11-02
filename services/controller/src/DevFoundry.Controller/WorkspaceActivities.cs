using Temporalio.Activities;

namespace DevFoundry.Controller;

public sealed class WorkspaceActivities(GitWorkspace workspace)
{
    [Activity("workspace_prepare")]
    public Task<WorkspaceInfo> PrepareAsync(WorkspaceRequest request) =>
        workspace.PrepareAsync(request, ActivityExecutionContext.Current.CancellationToken);

    [Activity("workspace_cleanup")]
    public Task CleanupAsync(string name) => workspace.CleanupAsync(name);
}
