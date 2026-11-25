namespace AiSdlcFactory.Controller;

public sealed class WorkerSettings
{
    public string TemporalAddress { get; init; } = "localhost:7233";
    public string TemporalNamespace { get; init; } = "default";
    public string TemporalTaskQueue { get; init; } = "factory-events";

    /// <summary>Every git host the factory serves (<c>FACTORY_HOSTS</c>): where it is, the bot's token and login on it, and how its tickets pick a workflow.</summary>
    public IReadOnlyList<HostSettings> Hosts { get; init; } = [];

    /// <summary>How long what the git host said a person may do on a repository is kept (<c>FACTORY_PERMISSION_CACHE_SECONDS</c>); 0 asks the host every time. Only used where a rule asks for access.</summary>
    public int PermissionCacheSeconds { get; init; } = 300;

    /// <summary>The name and e-mail address on the commits the factory makes (<c>FACTORY_GIT_AUTHOR_NAME</c>, <c>FACTORY_GIT_AUTHOR_EMAIL</c>). Use an address that belongs to the bot's account where you want its commits linked to it (on GitHub, the account's noreply address).</summary>
    public string GitAuthorName { get; init; } = "AI SDLC Factory";

    public string GitAuthorEmail { get; init; } = "factory-bot@ai-sdlc-factory.test";

    /// <summary>The model every agent uses unless its role file names another (<c>FACTORY_MODEL</c>); null means the default in <c>opencode.json</c>. The launcher applies it; the controller only records it.</summary>
    public string? Model { get; init; }

    /// <summary>The model of single roles (<c>FACTORY_ROLE_MODELS</c>), which ranks first: above the role file's <c>model:</c> line and <see cref="Model"/>. The launcher applies it; the controller records it and says it at start.</summary>
    public IReadOnlyDictionary<string, string> RoleModels { get; init; } = new Dictionary<string, string>();

    /// <summary>The host with this name, or a failure that retrying cannot mend: a repository named for a host that is not configured.</summary>
    public HostSettings HostNamed(string name) =>
        Hosts.FirstOrDefault(host => host.Name == name)
        ?? throw new Temporalio.Exceptions.ApplicationFailureException($"No git host named \"{name}\" is configured (see FACTORY_HOSTS).", nonRetryable: true);

    /// <summary>The host a qualified owner (<c>acme</c>, or <c>work:acme</c>) belongs to.</summary>
    public HostSettings HostFor(string qualifiedOwner) => HostNamed(RepoNames.Split(qualifiedOwner).Host);

    /// <summary>The login of the bot on the host a qualified owner belongs to: the factory only revises pull requests that account opened.</summary>
    public string BotLoginFor(string qualifiedOwner) => HostFor(qualifiedOwner).BotLogin;

    /// <summary>Every host's token that is set: what must never appear in a log, a record or a pushed file.</summary>
    public IReadOnlyList<string> HostTokens => Hosts.Select(host => host.Token).Where(token => !string.IsNullOrWhiteSpace(token)).ToList();

    /// <summary>Address of the Temporal web UI as a person opens it, used for the link to a run's full record.</summary>
    public string TemporalUiUrl { get; init; } = "http://localhost:8080";
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

    /// <summary>The weekly report on what repeats is made (see <c>FACTORY_IMPROVEMENT</c>); it only reads the host's logs and writes a file.</summary>
    public bool ImprovementEnabled { get; init; } = true;

    /// <summary>The repository (<c>owner/name</c>) whose tickets carry the weekly report and the agent's proposals, usually the factory's own; empty means the report is only saved to a file.</summary>
    public string ImprovementRepository { get; init; } = "";

    /// <summary>The egress proxy's log of refused hosts, if it writes one that this controller can read.</summary>
    public string EgressDeniedLog { get; init; } = "";

    /// <summary>What a project gets when its <c>limits</c> do not say (the <c>FACTORY_DEFAULT_*</c> settings).</summary>
    public FactoryDefaults Defaults { get; init; } = FactoryDefaults.Standard;

    public static WorkerSettings FromConfiguration(IConfiguration configuration) => new()
    {
        TemporalAddress = NonEmpty(configuration, "TEMPORAL_ADDRESS", "localhost:7233"),
        TemporalNamespace = NonEmpty(configuration, "TEMPORAL_NAMESPACE", "default"),
        TemporalTaskQueue = NonEmpty(configuration, "TEMPORAL_TASK_QUEUE", "factory-events"),
        TemporalUiUrl = NonEmpty(configuration, "TEMPORAL_UI_URL", "http://localhost:8080"),
        Hosts = HostsSetting.Parse(configuration[HostsSetting.Variable]).Select(host => new HostSettings(host.Name, host.Type, host.Url, host.Token, host.BotLogin, host.Triggers, host.Commands, host.Start)).ToList(),
        PermissionCacheSeconds = Number(configuration, "FACTORY_PERMISSION_CACHE_SECONDS", 300, v => v is >= 0 and <= 3600, "0 to 3600"),
        Model = ModelNames.Read(configuration[ModelNames.Variable]),
        RoleModels = EnabledProviders(KnownRoles(ModelNames.ReadRoles(configuration[ModelNames.RolesVariable])), configuration["FACTORY_AGENTS_DIR"]),
        GitAuthorName = Identity(configuration, "FACTORY_GIT_AUTHOR_NAME", "AI SDLC Factory"),
        GitAuthorEmail = Identity(configuration, "FACTORY_GIT_AUTHOR_EMAIL", "factory-bot@ai-sdlc-factory.test"),
        WorkspaceRoot = configuration["FACTORY_WORKSPACE_ROOT"] ?? "",
        AgentsDirectory = configuration["FACTORY_AGENTS_DIR"] ?? "",
        AllowRepoImages = Switch(configuration, "FACTORY_ALLOW_REPO_IMAGES"),
        JanitorEnabled = Switch(configuration, "FACTORY_JANITOR", fallback: true),
        JanitorIntervalHours = Number(configuration, "FACTORY_JANITOR_INTERVAL_HOURS", 24, v => v is >= 1 and <= 168, "1 to 168"),
        JanitorCheckoutDays = Number(configuration, "FACTORY_JANITOR_CHECKOUT_DAYS", 2, v => v is >= 1 and <= 30, "1 to 30"),
        JanitorWipDays = Number(configuration, "FACTORY_JANITOR_WIP_DAYS", 30, v => v is >= 1 and <= 365, "1 to 365"),
        JanitorDeleteWip = Switch(configuration, "FACTORY_JANITOR_DELETE_WIP"),
        ImprovementEnabled = Switch(configuration, "FACTORY_IMPROVEMENT", fallback: true),
        ImprovementRepository = Repository(configuration, "FACTORY_IMPROVEMENT_REPO"),
        EgressDeniedLog = configuration["FACTORY_EGRESS_DENIED_LOG"] ?? "",
        Defaults = new FactoryDefaults(
            Number(configuration, "FACTORY_DEFAULT_BUILD_ROUNDS", FactoryDefaults.Standard.BuildRounds, LimitRanges.BuildRoundsOk, $"{LimitRanges.MinBuildRounds} to {LimitRanges.MaxBuildRounds}"),
            Number(configuration, "FACTORY_DEFAULT_CLARIFICATION_ROUNDS", FactoryDefaults.Standard.ClarificationRounds, LimitRanges.ClarificationRoundsOk, $"{LimitRanges.MinClarificationRounds} to {LimitRanges.MaxClarificationRounds}"),
            Number(configuration, "FACTORY_DEFAULT_REVISIONS_PER_PULL_REQUEST", FactoryDefaults.Standard.RevisionsPerPullRequest, LimitRanges.RevisionsOk, $"{LimitRanges.MinRevisions} to {LimitRanges.MaxRevisions}"),
            MaxTokensPerRun: Tokens(configuration, "FACTORY_MAX_TOKENS_PER_RUN"),
            DailyTokenLimit: Tokens(configuration, "FACTORY_DAILY_TOKEN_LIMIT"),
            RepoDailyTokenLimit: Tokens(configuration, "FACTORY_REPO_DAILY_TOKEN_LIMIT"),
            ConfirmOverTokens: Tokens(configuration, "FACTORY_CONFIRM_OVER_TOKENS")),
    };

    /// <summary>A role's model must be on a provider the agents folder enables, or every run of that role would fail (see <see cref="ModelProviders"/>).</summary>
    private static IReadOnlyDictionary<string, string> EnabledProviders(IReadOnlyDictionary<string, string> models, string? agentsDirectory)
    {
        ModelProviders.RequireEnabled(models, agentsDirectory);
        return models;
    }

    /// <summary>The roles named in <c>FACTORY_ROLE_MODELS</c> must be roles of the factory: a misspelt one would otherwise change nothing, and the person would think it had.</summary>
    private static IReadOnlyDictionary<string, string> KnownRoles(IReadOnlyDictionary<string, string> models)
    {
        var unknown = models.Keys.Where(role => !AgentRoles.All.Contains(role)).ToList();
        return unknown.Count == 0
            ? models
            : throw new InvalidOperationException($"{ModelNames.RolesVariable} names {(unknown.Count == 1 ? "a role" : "roles")} the factory does not have: {string.Join(", ", unknown)}. The roles are {string.Join(", ", AgentRoles.All)}.");
    }

    /// <summary>A token limit: empty or 0 means no limit. Anything but a whole number from 10,000 up stops the start with a message, because a typo would otherwise switch the limit off or make every run stop at once.</summary>
    private static long Tokens(IConfiguration configuration, string key)
    {
        var text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }
        if (!long.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || (value != 0 && value < LimitRanges.MinTokenBudget) || value > 1_000_000_000_000)
        {
            throw new InvalidOperationException($"{key} must be 0 (no limit) or a whole number of tokens from {LimitRanges.MinTokenBudget:N0}, but it is \"{text}\".");
        }
        return value;
    }

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

    /// <summary>An <c>owner/name</c>, or empty. Anything else stops the start: a ticket opened in the wrong place cannot be taken back.</summary>
    private static string Repository(IConfiguration configuration, string key)
    {
        var text = configuration[key]?.Trim() ?? "";
        if (text.Length == 0)
        {
            return "";
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"\A([a-z][a-z0-9-]{0,30}:)?[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z") || text.Contains(".."))
        {
            throw new InvalidOperationException($"{key} must be a repository as owner/name (or host:owner/name for a host other than forgejo), but it is \"{text}\".");
        }
        return text;
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

    /// <summary>A name or address for commits: one line, no angle brackets (git would write them into the commit as they are), or the worker stops at start with a message.</summary>
    private static string Identity(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        value = value.Trim();
        if (value.Length > 200 || value.Any(c => char.IsControl(c) || c is '<' or '>'))
        {
            throw new InvalidOperationException($"{key} must be one line of at most 200 characters without < or >, but it is \"{value}\".");
        }
        return value;
    }

    private static string NonEmpty(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
