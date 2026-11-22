using Temporalio.Client;

namespace AiSdlcFactory.Controller;

public sealed record WorkspaceEntry(string Name, DateTimeOffset LastTouched);

/// <summary>What the janitor needs from the checkout folders.</summary>
public interface IWorkspaceStore
{
    IReadOnlyList<WorkspaceEntry> List();
    Task DeleteAsync(string name);
}

/// <summary>The ids of the workflows that are running right now.</summary>
public interface IRunningWorkflows
{
    Task<IReadOnlySet<string>> RunningIdsAsync(CancellationToken cancellationToken);
}

public sealed class TemporalRunningWorkflows(ITemporalClient client) : IRunningWorkflows
{
    public async Task<IReadOnlySet<string>> RunningIdsAsync(CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var execution in client.ListWorkflowsAsync("ExecutionStatus = \"Running\"").WithCancellation(cancellationToken))
        {
            ids.Add(execution.Id);
        }
        return ids;
    }
}

/// <summary>What the janitor needs from the git host: a few operations on repositories and branches. A narrow port of its own (it is part of <see cref="IGitHost"/>), so the janitor and its tests need nothing more.</summary>
public interface IJanitorGitHost
{
    Task<IReadOnlyList<(string Owner, string Repo)>> ListWritableRepositoriesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string owner, string repo, CancellationToken cancellationToken);
    Task<bool> IssueIsClosedAsync(string owner, string repo, int number, CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> OpenPullRequestBranchesAsync(string owner, string repo, CancellationToken cancellationToken);
    Task DeleteBranchAsync(string owner, string repo, string branch, CancellationToken cancellationToken);
}
