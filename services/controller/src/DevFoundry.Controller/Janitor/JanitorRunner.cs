namespace DevFoundry.Controller;

public sealed record JanitorReport(
    IReadOnlyList<string> RemovedCheckouts, IReadOnlyList<string> OldBranches, IReadOnlyList<string> DeletedBranches, IReadOnlyList<string> Problems);

/// <summary>
/// One cleaning pass. Cautious by design: a checkout goes only when its run is not running and it has been untouched for days; a work-in-progress
/// branch is only listed unless the operator allowed deletion, and is then deleted only when its ticket is closed and no open pull request uses it.
/// Anything it cannot establish (the running list, a ticket's state) means it does nothing for that item.
/// </summary>
public sealed class JanitorRunner(
    IWorkspaceStore workspaces, IRunningWorkflows running, IJanitorGitHost host, WorkerSettings settings, ILogger<JanitorRunner> log, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<JanitorReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        var old = new List<string>();
        var deleted = new List<string>();
        var problems = new List<string>();

        await CleanCheckoutsAsync(removed, problems, cancellationToken);
        await CheckBranchesAsync(old, deleted, problems, cancellationToken);

        log.LogInformation(
            "Janitor: removed {Removed} old checkout(s); {Old} old work-in-progress branch(es) found, {Deleted} deleted; {Problems} problem(s)",
            removed.Count, old.Count, deleted.Count, problems.Count);
        return new JanitorReport(removed, old, deleted, problems);
    }

    private async Task CleanCheckoutsAsync(List<string> removed, List<string> problems, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> runningIds;
        try
        {
            runningIds = await running.RunningIdsAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Without knowing what runs, deleting a checkout could pull it out from under a live run.
            problems.Add($"could not list the running workflows, so no checkout was removed: {error.Message}");
            log.LogWarning(error, "Janitor: could not list running workflows; skipping checkouts");
            return;
        }
        foreach (var entry in workspaces.List().Where(entry => JanitorRules.IsOurWorkspace(entry.Name)))
        {
            if (!JanitorRules.CheckoutIsStale(now() - entry.LastTouched, runningIds.Contains(entry.Name), settings.JanitorCheckoutDays))
            {
                continue;
            }
            try
            {
                await workspaces.DeleteAsync(entry.Name);
                removed.Add(entry.Name);
                log.LogInformation("Janitor: removed the leftover checkout {Name} (untouched since {Since:u})", entry.Name, entry.LastTouched);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                problems.Add($"could not remove the checkout {entry.Name}: {error.Message}");
                log.LogWarning(error, "Janitor: could not remove {Name}", entry.Name);
            }
        }
    }

    private async Task CheckBranchesAsync(List<string> old, List<string> deleted, List<string> problems, CancellationToken cancellationToken)
    {
        if (settings.HostTokens.Count == 0)
        {
            return;
        }
        IReadOnlyList<(string Owner, string Repo)> repositories;
        try
        {
            repositories = await host.ListWritableRepositoriesAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            problems.Add($"could not list the repositories: {error.Message}");
            log.LogWarning(error, "Janitor: could not list repositories");
            return;
        }

        foreach (var (owner, repo) in repositories)
        {
            try
            {
                var branches = (await host.ListBranchesAsync(owner, repo, cancellationToken))
                    .Where(branch => JanitorRules.WipIssueNumber(branch.Name) is not null && now() - branch.LastCommit >= TimeSpan.FromDays(settings.JanitorWipDays))
                    .ToList();
                if (branches.Count == 0)
                {
                    continue;
                }
                var openHeads = await host.OpenPullRequestBranchesAsync(owner, repo, cancellationToken);
                foreach (var branch in branches)
                {
                    var number = JanitorRules.WipIssueNumber(branch.Name)!.Value;
                    var closed = await host.IssueIsClosedAsync(owner, repo, number, cancellationToken);
                    var action = JanitorRules.DecideWip(now() - branch.LastCommit, closed, openHeads.Contains(branch.Name), settings.JanitorWipDays, settings.JanitorDeleteWip);
                    if (action == WipAction.Keep)
                    {
                        continue;
                    }
                    var days = (int)(now() - branch.LastCommit).TotalDays;
                    var label = $"{owner}/{repo}: {branch.Name}";
                    old.Add(label);
                    if (action == WipAction.Delete)
                    {
                        await host.DeleteBranchAsync(owner, repo, branch.Name, cancellationToken);
                        deleted.Add(label);
                        log.LogInformation("Janitor: deleted {Branch} in {Owner}/{Repo} ({Days} days old, ticket #{Number} is closed)", branch.Name, owner, repo, days, number);
                    }
                    else
                    {
                        log.LogInformation(
                            "Janitor: {Branch} in {Owner}/{Repo} is {Days} days old and ticket #{Number} is {State}; delete it when you no longer need the work",
                            branch.Name, owner, repo, days, number, closed ? "closed" : "still open");
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                problems.Add($"{owner}/{repo}: {error.Message}");
                log.LogWarning(error, "Janitor: problem with {Owner}/{Repo}", owner, repo);
            }
        }
    }
}
