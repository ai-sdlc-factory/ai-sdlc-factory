using System.Text.RegularExpressions;

namespace DevFoundry.OpenCodeWorker;

public sealed partial class Settings
{
    public string TemporalAddress { get; init; } = "localhost:7233";
    public string TemporalNamespace { get; init; } = "default";
    public string TemporalTaskQueue { get; init; } = "factory-events";
    public string OpenCodeTaskQueue { get; init; } = "opencode-tasks";
    public string OpenCodeModel { get; init; } = "openai/gpt-6.1-sol";
    public int OpenCodeTimeoutSeconds { get; set; } = 240;
    public string OpenCodeDataHome { get; init; } = "/data";

    public static Settings FromConfiguration(IConfiguration configuration)
    {
        var defaults = new Settings();
        var settings = new Settings
        {
            TemporalAddress = Value(configuration, "TEMPORAL_ADDRESS", defaults.TemporalAddress),
            TemporalNamespace = Value(configuration, "TEMPORAL_NAMESPACE", defaults.TemporalNamespace),
            TemporalTaskQueue = Value(configuration, "TEMPORAL_TASK_QUEUE", defaults.TemporalTaskQueue),
            OpenCodeTaskQueue = Value(configuration, "OPENCODE_TASK_QUEUE", defaults.OpenCodeTaskQueue),
            OpenCodeModel = Value(configuration, "OPENCODE_MODEL", defaults.OpenCodeModel),
            OpenCodeTimeoutSeconds = configuration.GetValue("OPENCODE_TIMEOUT_SECONDS", defaults.OpenCodeTimeoutSeconds),
            OpenCodeDataHome = Value(configuration, "OPENCODE_DATA_HOME", defaults.OpenCodeDataHome),
        };
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!ModelPattern().IsMatch(OpenCodeModel))
        {
            throw new InvalidOperationException("OPENCODE_MODEL must look like openai/<model>.");
        }
        if (OpenCodeTimeoutSeconds is < 1 or > 240)
        {
            throw new InvalidOperationException("OPENCODE_TIMEOUT_SECONDS must be between 1 and 240.");
        }
    }

    private static string Value(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    [GeneratedRegex(@"\Aopenai/\S+\z")]
    private static partial Regex ModelPattern();
}
