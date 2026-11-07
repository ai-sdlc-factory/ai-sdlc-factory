using System.Text.RegularExpressions;
using Temporalio.Client;

namespace DevFoundry.Controller;

public enum WipAction
{
    /// <summary>Too young, or something still uses it.</summary>
    Keep,

    /// <summary>Old enough to mention in the log, but not deleted.</summary>
    Report,

    /// <summary>Old, its ticket is closed, nothing uses it, and the operator allowed deletion.</summary>
    Delete,
}

/// <summary>The decisions of the janitor as plain functions, so the cautious rules can be tested without Forgejo or Docker.</summary>
public static partial class JanitorRules
{
    /// <summary>Only folders the factory itself makes (named after a workflow id) are ever removed; anything else in the root is not ours.</summary>
    private static readonly string[] OurPrefixes = [$"{WorkIds.Feature}-", $"{WorkIds.Bug}-", $"{WorkIds.Incident}-", $"{WorkIds.Revision}-"];

    [GeneratedRegex(@"\Afactory/issue-(?<number>\d+)-wip(?:-\d+)?\z")]
    private static partial Regex WipBranch();

    public static bool IsOurWorkspace(string name) => OurPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>A checkout is stale only if its run is gone <b>and</b> nothing touched it for the configured days.</summary>
    public static bool CheckoutIsStale(TimeSpan untouchedFor, bool runStillRunning, int days) =>
        !runStillRunning && untouchedFor >= TimeSpan.FromDays(days);

    /// <summary>The ticket number of a work-in-progress branch, or null for any other branch.</summary>
    public static int? WipIssueNumber(string branch) =>
        WipBranch().Match(branch) is { Success: true } match && int.TryParse(match.Groups["number"].Value, out var number) ? number : null;

    public static WipAction DecideWip(TimeSpan age, bool ticketClosed, bool hasOpenPullRequest, int days, bool deleteAllowed)
    {
        if (age < TimeSpan.FromDays(days) || hasOpenPullRequest)
        {
            return WipAction.Keep;
        }
        return deleteAllowed && ticketClosed ? WipAction.Delete : WipAction.Report;
    }
}

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

/// <summary>What the janitor needs from Forgejo.</summary>
public interface IJanitorForgejo
{
    Task<IReadOnlyList<(string Owner, string Repo)>> ListWritableRepositoriesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string owner, string repo, CancellationToken cancellationToken);
    Task<bool> IssueIsClosedAsync(string owner, string repo, int number, CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> OpenPullRequestBranchesAsync(string owner, string repo, CancellationToken cancellationToken);
    Task DeleteBranchAsync(string owner, string repo, string branch, CancellationToken cancellationToken);
}

public sealed record JanitorReport(
    IReadOnlyList<string> RemovedCheckouts, IReadOnlyList<string> OldBranches, IReadOnlyList<string> DeletedBranches, IReadOnlyList<string> Problems);

/// <summary>
/// One cleaning pass. Cautious by design: a checkout goes only when its run is not running and it has been untouched for days; a work-in-progress
/// branch is only listed unless the operator allowed deletion, and is then deleted only when its ticket is closed and no open pull request uses it.
/// Anything it cannot establish (the running list, a ticket's state) means it does nothing for that item.
/// </summary>
public sealed class JanitorRunner(
    IWorkspaceStore workspaces, IRunningWorkflows running, IJanitorForgejo forgejo, WorkerSettings settings, ILogger<JanitorRunner> log, Func<DateTimeOffset>? clock = null)
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
        if (string.IsNullOrWhiteSpace(settings.ForgejoToken))
        {
            return;
        }
        IReadOnlyList<(string Owner, string Repo)> repositories;
        try
        {
            repositories = await forgejo.ListWritableRepositoriesAsync(cancellationToken);
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
                var branches = (await forgejo.ListBranchesAsync(owner, repo, cancellationToken))
                    .Where(branch => JanitorRules.WipIssueNumber(branch.Name) is not null && now() - branch.LastCommit >= TimeSpan.FromDays(settings.JanitorWipDays))
                    .ToList();
                if (branches.Count == 0)
                {
                    continue;
                }
                var openHeads = await forgejo.OpenPullRequestBranchesAsync(owner, repo, cancellationToken);
                foreach (var branch in branches)
                {
                    var number = JanitorRules.WipIssueNumber(branch.Name)!.Value;
                    var closed = await forgejo.IssueIsClosedAsync(owner, repo, number, cancellationToken);
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
                        await forgejo.DeleteBranchAsync(owner, repo, branch.Name, cancellationToken);
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

/// <summary>Runs the janitor a few minutes after start and then every few hours, until the worker stops. A failed pass is logged and tried again next time.</summary>
public sealed class JanitorService(JanitorRunner runner, WorkerSettings settings, ILogger<JanitorService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.JanitorEnabled)
        {
            log.LogInformation("The janitor is switched off (FACTORY_JANITOR=false)");
            return;
        }
        try
        {
            // A restart storm should not trigger a pass each time, and nothing is urgent.
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(settings.JanitorIntervalHours));
            do
            {
                try
                {
                    await runner.RunOnceAsync(stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log.LogWarning(error, "Janitor pass failed; trying again next time");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
