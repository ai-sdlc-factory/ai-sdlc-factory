namespace DevFoundry.Controller;

/// <summary>
/// The agent roles the factory runs, one file each under <c>agents/agents/</c>. A test keeps this list equal to that folder, so a new role cannot be
/// added to one and forgotten in the other.
/// </summary>
public static class AgentRoles
{
    public static readonly IReadOnlyList<string> All =
    [
        "acceptance", "analyst", "architect", "asker", "bug-analyst", "configurer", "coverage", "developer", "diagnostician", "docs-writer", "e2e-tester", "improver",
        "incident-analyst", "investigator", "merge-resolver", "mitigator", "planner", "postmortem-writer", "pr-resolver", "reproducer", "reviewer", "security", "ux",
    ];

    /// <summary>The roles with no file in the agents folder the sandboxes get; empty when the folder is not known (nothing can be said then).</summary>
    public static IReadOnlyList<string> Missing(string? directory) =>
        string.IsNullOrWhiteSpace(directory)
            ? []
            : All.Where(role => !File.Exists($"{directory.TrimEnd('/')}/agents/{role}.md")).ToList();
}

/// <summary>
/// Says so in the log, at start, when role files are missing from the agents folder (usually an out-of-date copy of <c>agents/</c>): OpenCode would
/// otherwise answer as its default agent, and every run of those roles would fail with answers that look merely wrong. It does not stop the controller,
/// because the other roles still work.
/// </summary>
public sealed class AgentFolderCheck(WorkerSettings settings, ILogger<AgentFolderCheck> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var missing = AgentRoles.Missing(settings.AgentsDirectory);
        if (missing.Count > 0)
        {
            log.LogError(
                "The agents folder {Directory} has no role file for: {Roles}. Runs of those roles fail. It is probably an out-of-date copy of the repository's agents/ folder: point FACTORY_GLOBAL_CONFIG at the repository's own folder, or copy it again.",
                settings.AgentsDirectory, string.Join(", ", missing));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
