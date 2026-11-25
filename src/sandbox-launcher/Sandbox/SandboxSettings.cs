namespace AiSdlcFactory.SandboxLauncher;

/// <summary>
/// How sandboxes reach the internet. Enforce: only through the egress proxy, to the hosts of the run's profile
/// (the proxy may additionally run in audit mode). Open: no restrictions; sandboxes get a normal network.
/// </summary>
public enum EgressMode
{
    Enforce,
    Open,
}

public sealed class SandboxSettings
{
    public string TemporalAddress { get; init; } = "localhost:7233";
    public string TemporalNamespace { get; init; } = "default";
    public string TaskQueue { get; init; } = SandboxNames.DefaultTaskQueue;

    /// <summary>Absolute host path that holds one directory per workspace. Same path in every container.</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>Image aliases a request may choose from, mapped to concrete image names.</summary>
    public required IReadOnlyDictionary<string, string> Images { get; init; }

    /// <summary>
    /// Egress profile name to the internal Docker network a sandbox with that profile joins. The network has no
    /// route out; the only way off it is the profile's egress proxy. A run that asks for no profile gets no network.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Networks { get; init; }

    public EgressMode Egress { get; init; } = EgressMode.Enforce;

    /// <summary>The ordinary internet-connected network used when <see cref="Egress"/> is open. The launcher creates it.</summary>
    public string OpenNetwork { get; init; } = "ai-sdlc-factory_sandbox-open";

    /// <summary>Egress profile name to the address of that profile's proxy on its network, such as http://10.250.1.2:3128.</summary>
    public required IReadOnlyDictionary<string, string> Proxies { get; init; }

    /// <summary>Owner of the files in the workspace; the sandbox runs as this user.</summary>
    public int UserId { get; init; } = 1000;
    public int GroupId { get; init; } = 1000;

    public long MemoryBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public double Cpus { get; init; } = 2;
    public int PidsLimit { get; init; } = 1024;
    public int MaxRunSeconds { get; init; } = 1800;
    public int MaxConcurrentRuns { get; init; } = 2;
    public int MaxOutputBytes { get; init; } = 512 * 1024;

    /// <summary>Optional host directory with shared agent settings, mounted read-only at /etc/factory.</summary>
    public string? GlobalConfigPath { get; init; }

    public string DockerSocket { get; init; } = "/var/run/docker.sock";

    /// <summary>
    /// Whether a repository may bring its own sandbox image (<c>.factory/Dockerfile</c>). Off by default: building runs the repository's
    /// build instructions on this host's Docker, with internet access, which is the same trust as running its CI.
    /// </summary>
    public bool AllowRepoImages { get; init; }

    /// <summary>The longest a repository image build may take, and the largest image it may produce.</summary>
    public int MaxBuildSeconds { get; init; } = 1800;

    /// <summary>
    /// Repository images created more than this many days ago are removed once a day, if no container uses them. They are rebuilt on the next
    /// use, so removing one costs a build, never data. 0 keeps them forever.
    /// </summary>
    public int JanitorImageDays { get; init; } = 60;
    public long MaxRepoImageBytes { get; init; } = 6L * 1024 * 1024 * 1024;

    /// <summary>
    /// Docker volume that holds the OpenCode login. Mounted only into runs that ask for model access,
    /// so lint, build and test runs never see the credentials. Empty disables model runs.
    /// </summary>
    public string? AuthVolume { get; init; }

    /// <summary>The model for every agent whose role file names none (<c>FACTORY_MODEL</c>); null leaves the default in <c>opencode.json</c>. Given to model runs as OpenCode's inline configuration, which ranks above the file.</summary>
    public string? Model { get; init; }

    /// <summary>The model of single roles (<c>FACTORY_ROLE_MODELS</c>), set by the operator: it ranks above the role file's own <c>model:</c> line and above <see cref="Model"/>. Empty means every role uses what it did before.</summary>
    public IReadOnlyDictionary<string, string> RoleModels { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Read-only keeps the login unchangeable by an agent, but OpenCode cannot then save a refreshed
    /// token. Read-write is the default so a long-lived login keeps working.
    /// </summary>
    public bool AuthReadOnly { get; init; }

    public static SandboxSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = new SandboxSettings
        {
            TemporalAddress = NonEmpty(configuration, "TEMPORAL_ADDRESS", "localhost:7233"),
            TemporalNamespace = NonEmpty(configuration, "TEMPORAL_NAMESPACE", "default"),
            TaskQueue = NonEmpty(configuration, "SANDBOX_TASK_QUEUE", SandboxNames.DefaultTaskQueue),
            WorkspaceRoot = (configuration["FACTORY_WORKSPACE_ROOT"] ?? "").TrimEnd('/'),
            Images = ParseImages(configuration["SANDBOX_IMAGES"]),
            Networks = ParsePairs(configuration["SANDBOX_NETWORKS"], "SANDBOX_NETWORKS"),
            Proxies = ParsePairs(configuration["SANDBOX_PROXIES"], "SANDBOX_PROXIES"),
            Egress = ParseEgress(configuration["SANDBOX_EGRESS_MODE"]),
            OpenNetwork = NonEmpty(configuration, "SANDBOX_OPEN_NETWORK", "ai-sdlc-factory_sandbox-open"),
            UserId = configuration.GetValue("SANDBOX_UID", 1000),
            GroupId = configuration.GetValue("SANDBOX_GID", 1000),
            MemoryBytes = configuration.GetValue("SANDBOX_MEMORY_BYTES", 2L * 1024 * 1024 * 1024),
            Cpus = configuration.GetValue("SANDBOX_CPUS", 2d),
            PidsLimit = configuration.GetValue("SANDBOX_PIDS_LIMIT", 1024),
            MaxRunSeconds = configuration.GetValue("SANDBOX_MAX_RUN_SECONDS", 1800),
            MaxConcurrentRuns = configuration.GetValue("SANDBOX_MAX_CONCURRENT", 2),
            GlobalConfigPath = NullIfEmpty(configuration["FACTORY_GLOBAL_CONFIG"]),
            DockerSocket = NonEmpty(configuration, "DOCKER_SOCKET", "/var/run/docker.sock"),
            AuthVolume = NullIfEmpty(configuration["SANDBOX_AUTH_VOLUME"]),
            AuthReadOnly = configuration.GetValue("SANDBOX_AUTH_READONLY", false),
            Model = ModelNames.Read(configuration[ModelNames.Variable]),
            RoleModels = ModelNames.ReadRoles(configuration[ModelNames.RolesVariable]),
            AllowRepoImages = Switch(configuration, "FACTORY_ALLOW_REPO_IMAGES"),
            JanitorImageDays = configuration.GetValue("FACTORY_JANITOR_IMAGE_DAYS", 60),
        };
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (JanitorImageDays is < 0 or > 365)
        {
            throw new InvalidOperationException("FACTORY_JANITOR_IMAGE_DAYS must be from 0 (keep forever) to 365.");
        }
        if (!WorkspaceRoot.StartsWith('/') || WorkspaceRoot.Length < 2)
        {
            throw new InvalidOperationException("FACTORY_WORKSPACE_ROOT must be an absolute path.");
        }
        if (Images.Count == 0)
        {
            throw new InvalidOperationException("SANDBOX_IMAGES must list at least one alias=image pair.");
        }
        if (Networks.Count == 0 || !Networks.Keys.Order().SequenceEqual(Proxies.Keys.Order()))
        {
            throw new InvalidOperationException(
                "SANDBOX_NETWORKS and SANDBOX_PROXIES must list the same egress profiles, each with a network and a proxy.");
        }
        if (Networks.ContainsKey(SandboxNetworks.None))
        {
            throw new InvalidOperationException($"\"{SandboxNetworks.None}\" is reserved for runs without a network and cannot be a profile.");
        }
        foreach (var (profile, proxy) in Proxies)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(proxy, @"\Ahttp://[0-9]{1,3}(\.[0-9]{1,3}){3}:[0-9]{2,5}\z"))
            {
                throw new InvalidOperationException($"The proxy of profile \"{profile}\" must look like http://10.250.1.2:3128.");
            }
        }
        if (UserId <= 0 || GroupId <= 0)
        {
            throw new InvalidOperationException("SANDBOX_UID and SANDBOX_GID must not be root.");
        }
        if (GlobalConfigPath is { } path && !path.StartsWith('/'))
        {
            throw new InvalidOperationException("FACTORY_GLOBAL_CONFIG must be an absolute path.");
        }
    }

    /// <summary>Parses <c>alias=image;alias=image</c>.</summary>
    public static Dictionary<string, string> ParseImages(string? value) => ParsePairs(value, "SANDBOX_IMAGES");

    /// <summary>
    /// "enforce" and "audit" both mean the proxy decides (audit is the proxy's own switch); "open" lifts the
    /// restrictions. Anything else fails loudly: a typo must not silently change what is allowed.
    /// </summary>
    public static EgressMode ParseEgress(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "enforce" or "audit" => EgressMode.Enforce,
            "open" => EgressMode.Open,
            _ => throw new InvalidOperationException($"SANDBOX_EGRESS_MODE \"{value}\" is not one of enforce, audit, open."),
        };

    /// <summary>Parses <c>name=value;name=value</c>.</summary>
    public static Dictionary<string, string> ParsePairs(string? value, string setting)
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                throw new InvalidOperationException($"{setting} entries must look like name=value.");
            }
            images[parts[0]] = parts[1];
        }
        return images;
    }

    /// <summary>A safety switch: empty means off, "true" or "false" (any case) mean what they say, and anything else stops the start rather than guess.</summary>
    private static bool Switch(IConfiguration configuration, string key)
    {
        var text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        return bool.TryParse(text.Trim(), out var value) ? value : throw new InvalidOperationException($"{key} must be true or false, but it is \"{text}\".");
    }

    private static string NonEmpty(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
