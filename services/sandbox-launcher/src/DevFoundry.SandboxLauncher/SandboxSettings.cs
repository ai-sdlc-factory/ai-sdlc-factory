namespace DevFoundry.SandboxLauncher;

public sealed class SandboxSettings
{
    public string TemporalAddress { get; init; } = "localhost:7233";
    public string TemporalNamespace { get; init; } = "default";
    public string TaskQueue { get; init; } = SandboxNames.DefaultTaskQueue;

    /// <summary>Absolute host path that holds one directory per workspace. Same path in every container.</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>Image aliases a request may choose from, mapped to concrete image names.</summary>
    public required IReadOnlyDictionary<string, string> Images { get; init; }

    /// <summary>Docker network the sandboxes join. It must not contain the factory's own services.</summary>
    public string Network { get; init; } = "dev-foundry_sandbox";

    /// <summary>Owner of the files in the workspace; the sandbox runs as this user.</summary>
    public int UserId { get; init; } = 1000;
    public int GroupId { get; init; } = 1000;

    public long MemoryBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public double Cpus { get; init; } = 2;
    public int PidsLimit { get; init; } = 256;
    public int MaxRunSeconds { get; init; } = 1800;
    public int MaxConcurrentRuns { get; init; } = 2;
    public int MaxOutputBytes { get; init; } = 64 * 1024;

    /// <summary>Optional host directory with shared agent settings, mounted read-only at /etc/factory.</summary>
    public string? GlobalConfigPath { get; init; }

    public string DockerSocket { get; init; } = "/var/run/docker.sock";

    public static SandboxSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = new SandboxSettings
        {
            TemporalAddress = NonEmpty(configuration, "TEMPORAL_ADDRESS", "localhost:7233"),
            TemporalNamespace = NonEmpty(configuration, "TEMPORAL_NAMESPACE", "default"),
            TaskQueue = NonEmpty(configuration, "SANDBOX_TASK_QUEUE", SandboxNames.DefaultTaskQueue),
            WorkspaceRoot = (configuration["FACTORY_WORKSPACE_ROOT"] ?? "").TrimEnd('/'),
            Images = ParseImages(configuration["SANDBOX_IMAGES"]),
            Network = NonEmpty(configuration, "SANDBOX_NETWORK", "dev-foundry_sandbox"),
            UserId = configuration.GetValue("SANDBOX_UID", 1000),
            GroupId = configuration.GetValue("SANDBOX_GID", 1000),
            MemoryBytes = configuration.GetValue("SANDBOX_MEMORY_BYTES", 2L * 1024 * 1024 * 1024),
            Cpus = configuration.GetValue("SANDBOX_CPUS", 2d),
            PidsLimit = configuration.GetValue("SANDBOX_PIDS_LIMIT", 256),
            MaxRunSeconds = configuration.GetValue("SANDBOX_MAX_RUN_SECONDS", 1800),
            MaxConcurrentRuns = configuration.GetValue("SANDBOX_MAX_CONCURRENT", 2),
            GlobalConfigPath = NullIfEmpty(configuration["FACTORY_GLOBAL_CONFIG"]),
            DockerSocket = NonEmpty(configuration, "DOCKER_SOCKET", "/var/run/docker.sock"),
        };
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!WorkspaceRoot.StartsWith('/') || WorkspaceRoot.Length < 2)
        {
            throw new InvalidOperationException("FACTORY_WORKSPACE_ROOT must be an absolute path.");
        }
        if (Images.Count == 0)
        {
            throw new InvalidOperationException("SANDBOX_IMAGES must list at least one alias=image pair.");
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
    public static Dictionary<string, string> ParseImages(string? value)
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                throw new InvalidOperationException("SANDBOX_IMAGES entries must look like alias=image.");
            }
            images[parts[0]] = parts[1];
        }
        return images;
    }

    private static string NonEmpty(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
