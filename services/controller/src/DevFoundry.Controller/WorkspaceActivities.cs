using Temporalio.Activities;

namespace DevFoundry.Controller;

public sealed class WorkspaceActivities(GitWorkspace workspace, WorkerSettings settings)
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

    [Activity("workspace_rescue")]
    public Task<RescueResult> RescueAsync(RescueRequest request)
    {
        var info = ActivityExecutionContext.Current.Info;
        // Where the whole run can be read back: every prompt, agent answer and check output is in Temporal's history.
        var trace = $"{settings.TemporalUiUrl.TrimEnd('/')}/namespaces/{Uri.EscapeDataString(settings.TemporalNamespace)}"
            + $"/workflows/{Uri.EscapeDataString(info.WorkflowId ?? "")}/{info.WorkflowRunId}/history";
        return workspace.RescueAsync(request, trace, ActivityExecutionContext.Current.CancellationToken);
    }

    [Activity("workspace_publish")]
    public Task<PublishResult?> PublishAsync(PublishRequest request) =>
        workspace.PublishAsync(request, ActivityExecutionContext.Current.CancellationToken);
}
