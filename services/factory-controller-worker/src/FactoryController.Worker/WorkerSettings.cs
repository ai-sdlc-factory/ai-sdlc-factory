namespace FactoryController.Worker;

public sealed class WorkerSettings
{
    public string TemporalAddress { get; init; } = "localhost:7233";
    public string TemporalNamespace { get; init; } = "default";
    public string TemporalTaskQueue { get; init; } = "factory-events";

    public static WorkerSettings FromConfiguration(IConfiguration configuration) => new()
    {
        TemporalAddress = NonEmpty(configuration, "TEMPORAL_ADDRESS", "localhost:7233"),
        TemporalNamespace = NonEmpty(configuration, "TEMPORAL_NAMESPACE", "default"),
        TemporalTaskQueue = NonEmpty(configuration, "TEMPORAL_TASK_QUEUE", "factory-events"),
    };

    private static string NonEmpty(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
