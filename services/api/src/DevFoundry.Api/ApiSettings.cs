namespace DevFoundry.Api;

public sealed class ApiSettings
{
    public string TemporalAddress { get; init; } = "localhost:7233";
    public string TemporalNamespace { get; init; } = "default";
    public string TemporalTaskQueue { get; init; } = "factory-events";
    public required string ForgejoWebhookSecret { get; init; }
    public int MaxWebhookBytes { get; init; } = 1_048_576;

    /// <summary>The secret a host's webhooks are signed with, or null for a host this API has no secret for (its route then answers 404). One setting per host, named after it.</summary>
    public string? WebhookSecretFor(string host) =>
        string.Equals(host, ForgejoWebhookAdapter.HostName, StringComparison.OrdinalIgnoreCase) ? ForgejoWebhookSecret : null;

    public static ApiSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = new ApiSettings
        {
            TemporalAddress = NonEmpty(configuration, "TEMPORAL_ADDRESS", "localhost:7233"),
            TemporalNamespace = NonEmpty(configuration, "TEMPORAL_NAMESPACE", "default"),
            TemporalTaskQueue = NonEmpty(configuration, "TEMPORAL_TASK_QUEUE", "factory-events"),
            ForgejoWebhookSecret = configuration["FORGEJO_WEBHOOK_SECRET"] ?? "",
            MaxWebhookBytes = configuration.GetValue("MAX_WEBHOOK_BYTES", 1_048_576),
        };
        settings.Validate();
        return settings;
    }

    // Messages name the setting but never echo its value.
    public void Validate()
    {
        if (ForgejoWebhookSecret.Length < 32)
        {
            throw new InvalidOperationException(
                "FORGEJO_WEBHOOK_SECRET must be set to at least 32 characters.");
        }
        if (MaxWebhookBytes is < 1 or > 1_048_576)
        {
            throw new InvalidOperationException("MAX_WEBHOOK_BYTES must be between 1 and 1048576.");
        }
    }

    private static string NonEmpty(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
