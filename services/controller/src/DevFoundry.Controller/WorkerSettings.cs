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
    /// <summary>The bot account's login: the factory only revises pull requests this account opened. Not read from the API, which would need a wider token.</summary>
    public string ForgejoBotLogin { get; init; } = "factory-bot";
    /// <summary>Absolute host path where per-run checkouts live; the same path inside every container.</summary>
    public string WorkspaceRoot { get; init; } = "";
    /// <summary>The factory's agent folder (read-only), used only to name the model each role is configured with.</summary>
    public string AgentsDirectory { get; init; } = "";

    /// <summary>
    /// Whether a repository may bring its own sandbox image (<c>.factory/Dockerfile</c>). Off by default. The launcher has the same switch and
    /// refuses to build when it is off; this one decides what the workflows tell people.
    /// </summary>
    public bool AllowRepoImages { get; init; }

    /// <summary>The janitor: removes checkouts of runs that are gone and reports (or, if told to, deletes) old work-in-progress branches.</summary>
    public bool JanitorEnabled { get; init; } = true;
    public int JanitorIntervalHours { get; init; } = 24;
    /// <summary>A checkout whose run is no longer running is removed when it has been untouched this many days.</summary>
    public int JanitorCheckoutDays { get; init; } = 2;
    /// <summary>A work-in-progress branch is reported once its last commit is this many days old.</summary>
    public int JanitorWipDays { get; init; } = 30;
    /// <summary>Delete reported branches whose ticket is closed. Off: they are only listed in the log.</summary>
    public bool JanitorDeleteWip { get; init; }

    /// <summary>What a project gets when its <c>limits</c> do not say (the <c>FACTORY_DEFAULT_*</c> settings).</summary>
    public FactoryDefaults Defaults { get; init; } = FactoryDefaults.Standard;

    public static WorkerSettings FromConfiguration(IConfiguration configuration) => new()
    {
        TemporalAddress = NonEmpty(configuration, "TEMPORAL_ADDRESS", "localhost:7233"),
        TemporalNamespace = NonEmpty(configuration, "TEMPORAL_NAMESPACE", "default"),
        TemporalTaskQueue = NonEmpty(configuration, "TEMPORAL_TASK_QUEUE", "factory-events"),
        ForgejoUrl = NonEmpty(configuration, "FORGEJO_URL", "http://forgejo:3000"),
        TemporalUiUrl = NonEmpty(configuration, "TEMPORAL_UI_URL", "http://localhost:8080"),
        ForgejoToken = configuration["FORGEJO_BOT_TOKEN"] ?? "",
        ForgejoBotLogin = NonEmpty(configuration, "FORGEJO_BOT_LOGIN", "factory-bot"),
        WorkspaceRoot = configuration["FACTORY_WORKSPACE_ROOT"] ?? "",
        AgentsDirectory = configuration["FACTORY_AGENTS_DIR"] ?? "",
        AllowRepoImages = Switch(configuration, "FACTORY_ALLOW_REPO_IMAGES"),
        JanitorEnabled = Switch(configuration, "FACTORY_JANITOR", fallback: true),
        JanitorIntervalHours = Number(configuration, "FACTORY_JANITOR_INTERVAL_HOURS", 24, v => v is >= 1 and <= 168, "1 to 168"),
        JanitorCheckoutDays = Number(configuration, "FACTORY_JANITOR_CHECKOUT_DAYS", 2, v => v is >= 1 and <= 30, "1 to 30"),
        JanitorWipDays = Number(configuration, "FACTORY_JANITOR_WIP_DAYS", 30, v => v is >= 1 and <= 365, "1 to 365"),
        JanitorDeleteWip = Switch(configuration, "FACTORY_JANITOR_DELETE_WIP"),
        Defaults = new FactoryDefaults(
            Number(configuration, "FACTORY_DEFAULT_BUILD_ROUNDS", FactoryDefaults.Standard.BuildRounds, LimitRanges.BuildRoundsOk, $"{LimitRanges.MinBuildRounds} to {LimitRanges.MaxBuildRounds}"),
            Number(configuration, "FACTORY_DEFAULT_CLARIFICATION_ROUNDS", FactoryDefaults.Standard.ClarificationRounds, LimitRanges.ClarificationRoundsOk, $"{LimitRanges.MinClarificationRounds} to {LimitRanges.MaxClarificationRounds}"),
            Number(configuration, "FACTORY_DEFAULT_REVISIONS_PER_PULL_REQUEST", FactoryDefaults.Standard.RevisionsPerPullRequest, LimitRanges.RevisionsOk, $"{LimitRanges.MinRevisions} to {LimitRanges.MaxRevisions}")),
    };

    /// <summary>A default that is not a whole number in range stops the worker at start with a message, instead of changing behaviour quietly.</summary>
    private static int Number(IConfiguration configuration, string key, int fallback, Func<int, bool> inRange, string range)
    {
        var text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || !inRange(value))
        {
            throw new InvalidOperationException($"{key} must be a whole number from {range}, but it is \"{text}\".");
        }
        return value;
    }

    /// <summary>A safety switch: empty means off, "true" or "false" (any case) mean what they say, and anything else stops the start rather than guess.</summary>
    private static bool Switch(IConfiguration configuration, string key, bool fallback = false)
    {
        var text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }
        return bool.TryParse(text.Trim(), out var value) ? value : throw new InvalidOperationException($"{key} must be true or false, but it is \"{text}\".");
    }

    private static string NonEmpty(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
