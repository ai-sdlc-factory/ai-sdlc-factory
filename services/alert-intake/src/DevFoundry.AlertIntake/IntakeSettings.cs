namespace DevFoundry.AlertIntake;

/// <summary>
/// What the alert intake reads from its environment. Messages name a setting but never echo its value.
/// </summary>
public sealed class IntakeSettings
{
    public required string Token { get; init; }
    public string ForgejoUrl { get; init; } = "http://forgejo:3000";
    public string ForgejoToken { get; init; } = "";
    public string MappingFile { get; init; } = "/etc/alert-intake/mapping.json";

    /// <summary>When true nothing is written to Forgejo: the ticket that would be opened is only logged.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Where Loki answers, for the log lines a route adds to its tickets. Empty means no log evidence.</summary>
    public string LokiUrl { get; init; } = "";

    public int MaxTicketsPerHour { get; init; } = 6;

    /// <summary>An alert for a route that already has an open ticket from the last this-many minutes is added to that ticket as a comment, not opened as a new one (one outage often raises several alerts). 0 switches it off.</summary>
    public int GroupMinutes { get; init; } = 15;
    public int MaxBodyBytes { get; init; } = 262_144;

    public static IntakeSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = new IntakeSettings
        {
            Token = configuration["ALERT_INTAKE_TOKEN"] ?? "",
            ForgejoUrl = NonEmpty(configuration, "FORGEJO_URL", "http://forgejo:3000"),
            ForgejoToken = configuration["FORGEJO_BOT_TOKEN"] ?? "",
            MappingFile = NonEmpty(configuration, "ALERT_INTAKE_MAPPING_FILE", "/etc/alert-intake/mapping.json"),
            DryRun = configuration.GetValue("ALERT_INTAKE_DRY_RUN", true),
            LokiUrl = configuration["ALERT_INTAKE_LOKI_URL"] ?? "",
            MaxTicketsPerHour = configuration.GetValue("ALERT_INTAKE_MAX_TICKETS_PER_HOUR", 6),
            GroupMinutes = configuration.GetValue("ALERT_INTAKE_GROUP_MINUTES", 15),
            MaxBodyBytes = configuration.GetValue("ALERT_INTAKE_MAX_BODY_BYTES", 262_144),
        };
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (Token.Length < 16)
        {
            throw new InvalidOperationException("ALERT_INTAKE_TOKEN must be set to at least 16 characters.");
        }
        if (!DryRun && string.IsNullOrWhiteSpace(ForgejoToken))
        {
            throw new InvalidOperationException("FORGEJO_BOT_TOKEN must be set when ALERT_INTAKE_DRY_RUN is false.");
        }
        if (!Uri.TryCreate(ForgejoUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("FORGEJO_URL must be an http or https address.");
        }
        if (LokiUrl.Length > 0 && (!Uri.TryCreate(LokiUrl, UriKind.Absolute, out var loki) || loki.Scheme is not ("http" or "https")))
        {
            throw new InvalidOperationException("ALERT_INTAKE_LOKI_URL must be an http or https address.");
        }
        if (MaxTicketsPerHour is < 1 or > 100)
        {
            throw new InvalidOperationException("ALERT_INTAKE_MAX_TICKETS_PER_HOUR must be between 1 and 100.");
        }
        if (GroupMinutes is < 0 or > 1_440)
        {
            throw new InvalidOperationException("ALERT_INTAKE_GROUP_MINUTES must be between 0 and 1440.");
        }
        if (MaxBodyBytes is < 1_024 or > 1_048_576)
        {
            throw new InvalidOperationException("ALERT_INTAKE_MAX_BODY_BYTES must be between 1024 and 1048576.");
        }
    }

    private static string NonEmpty(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
