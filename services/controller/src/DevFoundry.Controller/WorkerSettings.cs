namespace DevFoundry.Controller;

public sealed class WorkerSettings
{
    public string TemporalAddress { get; init; } = "localhost:7233";
    public string TemporalNamespace { get; init; } = "default";
    public string TemporalTaskQueue { get; init; } = "factory-events";
    public string ForgejoUrl { get; init; } = "http://forgejo:3000";
    /// <summary>Access token of the factory's Forgejo bot account; empty disables ticket actions.</summary>
    public string ForgejoToken { get; init; } = "";

    public static WorkerSettings FromConfiguration(IConfiguration configuration) => new()
    {
        TemporalAddress = NonEmpty(configuration, "TEMPORAL_ADDRESS", "localhost:7233"),
        TemporalNamespace = NonEmpty(configuration, "TEMPORAL_NAMESPACE", "default"),
        TemporalTaskQueue = NonEmpty(configuration, "TEMPORAL_TASK_QUEUE", "factory-events"),
        ForgejoUrl = NonEmpty(configuration, "FORGEJO_URL", "http://forgejo:3000"),
        ForgejoToken = configuration["FORGEJO_BOT_TOKEN"] ?? "",
    };

    private static string NonEmpty(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
