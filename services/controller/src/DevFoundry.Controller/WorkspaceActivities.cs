using Temporalio.Activities;

namespace DevFoundry.Controller;

public sealed class WorkspaceActivities(GitWorkspace workspace)
{
    [Activity("workspace_prepare")]
    public Task<WorkspaceInfo> PrepareAsync(WorkspaceRequest request) =>
        workspace.PrepareAsync(request, ActivityExecutionContext.Current.CancellationToken);

    [Activity("workspace_cleanup")]
    public Task CleanupAsync(string name) => workspace.CleanupAsync(name);

    [Activity("workspace_read_project_config")]
    public Task<string?> ReadProjectConfigAsync(string name) => Task.FromResult(workspace.ReadProjectConfig(name));

    [Activity("workspace_changes")]
    public Task<ChangeReport> DetectChangesAsync(string name) =>
        workspace.DetectChangesAsync(name, ActivityExecutionContext.Current.CancellationToken);

    [Activity("workspace_diff")]
    public Task<string> DiffAsync(string name) =>
        workspace.DiffAsync(name, ActivityExecutionContext.Current.CancellationToken);

    [Activity("workspace_publish")]
    public Task<PublishResult?> PublishAsync(PublishRequest request) =>
        workspace.PublishAsync(request, ActivityExecutionContext.Current.CancellationToken);
}
