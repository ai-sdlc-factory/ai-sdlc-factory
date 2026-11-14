using Temporalio.Activities;

namespace DevFoundry.Controller;

public sealed class WorkspaceActivities(GitWorkspace workspace, WorkerSettings settings)
{
    [Activity("workspace_prepare")]
    public Task<WorkspaceInfo> PrepareAsync(WorkspaceRequest request) =>
        workspace.PrepareAsync(request, ActivityExecutionContext.Current.CancellationToken);

    /// <summary>The factory's default limits, read through an activity so a run keeps the values it started with even if the worker is restarted with others.</summary>
    [Activity("factory_defaults")]
    public Task<FactoryDefaults> GetDefaultsAsync() => Task.FromResult(settings.Defaults with { AllowRepoImages = settings.AllowRepoImages });

    /// <summary>Records the extra protected paths a project asked for, where the trusted checkout can see them and an agent cannot.</summary>
    [Activity("workspace_set_protected_paths")]
    public Task SetProtectedPathsAsync(ProtectedPathsRequest request)
    {
        workspace.SetProtectedPaths(request.Name, request.Patterns);
        return Task.CompletedTask;
    }

    [Activity("workspace_cleanup")]
    public Task CleanupAsync(string name) => workspace.CleanupAsync(name);

    [Activity("workspace_read_project_config")]
    public Task<string?> ReadProjectConfigAsync(string name) => Task.FromResult(workspace.ReadProjectConfig(name));

    /// <summary>The repository's own sandbox image definition (<c>.factory/Dockerfile</c>), or null.</summary>
    [Activity("workspace_read_project_image")]
    public Task<RepoImageSource?> ReadProjectImageAsync(string name) => Task.FromResult(workspace.ReadProjectImage(name));

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

    [Activity("workspace_record")]
    public Task RecordAsync(RecordEntry entry) => workspace.RecordAsync(entry);

    [Activity("workspace_publish")]
    public Task<PublishResult?> PublishAsync(PublishRequest request) =>
        workspace.PublishAsync(request, ActivityExecutionContext.Current.CancellationToken);

    /// <summary>Merges the base branch into the pull request's branch in the trusted clone and gives the agent's copy the result (an <c>/update</c>).</summary>
    [Activity("workspace_merge_base")]
    public Task<MergeResult> MergeBaseAsync(MergeBaseRequest request) =>
        workspace.MergeBaseAsync(request, ActivityExecutionContext.Current.CancellationToken);

    /// <summary>The conflicted files that still hold conflict markers.</summary>
    [Activity("workspace_conflict_markers")]
    public Task<IReadOnlyList<string>> FindConflictMarkersAsync(string name) => workspace.FindConflictMarkersAsync(name);

    [Activity("workspace_publish_merge")]
    public Task<PublishResult> PublishMergeAsync(PublishMergeRequest request) =>
        workspace.PublishMergeAsync(request, ActivityExecutionContext.Current.CancellationToken);

    /// <summary>Adds a finished run to the usage log (one line: the run, what it was about, the tokens its agents used).</summary>
    [Activity("workspace_log_usage")]
    public Task LogUsageAsync(UsageLogEntry entry) => workspace.LogUsageAsync(entry, DateTimeOffset.UtcNow);

    /// <summary>What the runs that finished today used, for the daily token limits.</summary>
    [Activity("workspace_usage_today")]
    public Task<UsageToday> UsageTodayAsync(string repository) => Task.FromResult(workspace.UsageToday(repository, DateTimeOffset.UtcNow));

    /// <summary>Keeps the stages a run has finished, for a later <c>/retry</c>.</summary>
    [Activity("workspace_save_resume")]
    public Task SaveResumeAsync(SaveResumeRequest request) => workspace.SaveResumeAsync(request);

    [Activity("workspace_load_resume")]
    public Task<ResumeState?> LoadResumeAsync(string name) => Task.FromResult(workspace.LoadResume(name));

    [Activity("workspace_clear_resume")]
    public Task ClearResumeAsync(string name)
    {
        workspace.ClearResume(name);
        return Task.CompletedTask;
    }
}
