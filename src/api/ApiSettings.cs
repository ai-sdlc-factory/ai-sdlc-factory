namespace DevFoundry.Api;

/// <summary>A git host this API takes webhooks from: its name (the end of its route), the kind of host it is, and the secret its deliveries are signed with.</summary>
public sealed record WebhookHost(string Name, string Type, string Secret);

public sealed class ApiSettings
{
    public string TemporalAddress { get; init; } = "localhost:7233";
    public string TemporalNamespace { get; init; } = "default";
    public string TemporalTaskQueue { get; init; } = "factory-events";
    public int MaxWebhookBytes { get; init; } = 1_048_576;

    /// <summary>The hosts this API takes webhooks from (<c>FACTORY_HOSTS</c>): each has a route <c>/webhooks/&lt;name&gt;</c>, its own kind and its own secret.</summary>
    public IReadOnlyList<WebhookHost> Hosts { get; init; } = [];

    /// <summary>The secret a host's webhooks are signed with, or null for a host this API has none for (its route then answers 404).</summary>
    public string? WebhookSecretFor(string host) => Hosts.FirstOrDefault(known => known.Name == host)?.Secret;

    public static ApiSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = new ApiSettings
        {
            TemporalAddress = NonEmpty(configuration, "TEMPORAL_ADDRESS", "localhost:7233"),
            TemporalNamespace = NonEmpty(configuration, "TEMPORAL_NAMESPACE", "default"),
            TemporalTaskQueue = NonEmpty(configuration, "TEMPORAL_TASK_QUEUE", "factory-events"),
            MaxWebhookBytes = configuration.GetValue("MAX_WEBHOOK_BYTES", 1_048_576),
            Hosts = HostsSetting.Parse(configuration[HostsSetting.Variable], needWebhookSecret: true).Select(host => new WebhookHost(host.Name, host.Type, host.WebhookSecret)).ToList(),
        };
        settings.Validate();
        return settings;
    }

    // Messages name the setting but never echo its value.
    public void Validate()
    {
        if (Hosts.Count == 0 || Hosts.Any(host => host.Secret.Length < 32))
        {
            throw new InvalidOperationException(
                "FACTORY_HOSTS must list at least one git host, and every host needs a webhook_secret of at least 32 characters.");
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
