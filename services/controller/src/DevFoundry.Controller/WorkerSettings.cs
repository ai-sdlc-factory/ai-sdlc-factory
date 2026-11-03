namespace DevFoundry.Controller;

public sealed class WorkerSettings
{
    public string TemporalAddress { get; init; } = "localhost:7233";
    public string TemporalNamespace { get; init; } = "default";
    public string TemporalTaskQueue { get; init; } = "factory-events";
    public string ForgejoUrl { get; init; } = "http://forgejo:3000";

    /// <summary>Address of the Temporal web UI as a person opens it, used for the link to a run's full record.</summary>
    public string TemporalUiUrl { get; init; } = "http://localhost:8080";
    /// <summary>Access token of the factory's Forgejo bot account; empty disables ticket actions.</summary>
    public string ForgejoToken { get; init; } = "";
    /// <summary>Absolute host path where per-run checkouts live; the same path inside every container.</summary>
    public string WorkspaceRoot { get; init; } = "";
    /// <summary>The factory's agent folder (read-only), used only to name the model each role is configured with.</summary>
    public string AgentsDirectory { get; init; } = "";

    public static WorkerSettings FromConfiguration(IConfiguration configuration) => new()
    {
        TemporalAddress = NonEmpty(configuration, "TEMPORAL_ADDRESS", "localhost:7233"),
        TemporalNamespace = NonEmpty(configuration, "TEMPORAL_NAMESPACE", "default"),
        TemporalTaskQueue = NonEmpty(configuration, "TEMPORAL_TASK_QUEUE", "factory-events"),
        ForgejoUrl = NonEmpty(configuration, "FORGEJO_URL", "http://forgejo:3000"),
        TemporalUiUrl = NonEmpty(configuration, "TEMPORAL_UI_URL", "http://localhost:8080"),
        ForgejoToken = configuration["FORGEJO_BOT_TOKEN"] ?? "",
        WorkspaceRoot = configuration["FACTORY_WORKSPACE_ROOT"] ?? "",
        AgentsDirectory = configuration["FACTORY_AGENTS_DIR"] ?? "",
    };

    private static string NonEmpty(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
