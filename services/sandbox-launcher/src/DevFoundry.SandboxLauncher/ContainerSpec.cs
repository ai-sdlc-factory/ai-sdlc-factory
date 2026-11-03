using System.Text.Json.Serialization;

namespace DevFoundry.SandboxLauncher;

/// <summary>
/// Builds the Docker create-container body. All isolation settings are fixed here, in one place,
/// and nothing in a request can change them.
/// </summary>
public static class ContainerSpec
{
    public const string Label = "dev-foundry.sandbox";
    public const string WorkspaceLabel = "dev-foundry.workspace";
    public const string RoleLabel = "dev-foundry.role";
    public const string WorkspaceTarget = "/workspace";
    public const string GlobalConfigTarget = "/etc/factory";
    public const string AuthTarget = "/auth";

    /// <summary>
    /// What <c>docker ps --filter label=...</c> can tell apart: which ticket's workspace (its name is the workflow id)
    /// and which role the container belongs to. Both were validated already.
    /// </summary>
    private static Dictionary<string, string> Labels(ValidatedRun run)
    {
        var labels = new Dictionary<string, string> { [Label] = "true" };
        if (run.Workspace is not null)
        {
            labels[WorkspaceLabel] = run.Workspace;
        }
        if (run.Role is not null)
        {
            labels[RoleLabel] = run.Role;
        }
        return labels;
    }

    public static CreateContainerBody Build(ValidatedRun run, SandboxSettings settings)
    {
        var mounts = new List<MountSpec>
        {
            new("bind", run.WorkspacePath, WorkspaceTarget, ReadOnly: !run.WorkspaceWritable),
        };
        if (settings.GlobalConfigPath is { } globalConfig)
        {
            mounts.Add(new MountSpec("bind", globalConfig, GlobalConfigTarget, ReadOnly: true));
        }

        var env = new List<string>(run.Env);
        // OpenCode otherwise fetches its model list, checks for updates and downloads language servers at run
        // time; none of that is needed, and each would need another host on the allowlist.
        env.AddRange(["OPENCODE_DISABLE_MODELS_FETCH=1", "OPENCODE_DISABLE_AUTOUPDATE=1", "OPENCODE_DISABLE_LSP_DOWNLOAD=1"]);
        // In open mode a run that wants a network gets an ordinary one, with no proxy; a run that asked for none
        // still gets none.
        var open = settings.Egress == EgressMode.Open && run.Network != SandboxNetworks.None;
        if (run.Network != SandboxNetworks.None && !open)
        {
            // The sandbox's network has no route out. Tools that honour these variables reach the allowlisted hosts
            // through the profile's egress proxy; anything that ignores them simply cannot connect.
            var proxy = settings.Proxies[run.Network];
            // OpenCode tries to install helper packages from the npm registry at start (they are not needed). Agents
            // that only read code never run npm, so point npm at a dead address and spare them the failed attempts.
            // The developer and the project's checks keep the real registry, because projects install from it.
            if (run.Network == SandboxNetworks.Model)
            {
                env.Add("NPM_CONFIG_REGISTRY=http://127.0.0.1:9");
            }
            env.AddRange([$"HTTP_PROXY={proxy}", $"HTTPS_PROXY={proxy}", $"http_proxy={proxy}", $"https_proxy={proxy}", "NO_PROXY=localhost,127.0.0.1", "no_proxy=localhost,127.0.0.1"]);
        }
        if (run.NeedsModelAuth)
        {
            // NoCopy: never seed the volume from image content. The entrypoint links auth.json from here.
            mounts.Add(new MountSpec("volume", settings.AuthVolume!, AuthTarget, settings.AuthReadOnly, new VolumeOptionsSpec(true)));
            env.Add("FACTORY_REQUIRE_AUTH=1");
        }

        return new CreateContainerBody
        {
            Image = run.Image,
            Cmd = run.Command,
            User = $"{settings.UserId}:{settings.GroupId}",
            WorkingDir = WorkspaceTarget,
            Env = [.. env, "HOME=/tmp/home", "XDG_CACHE_HOME=/tmp/cache", "XDG_CONFIG_HOME=/tmp/config"],
            Labels = Labels(run),
            HostConfig = new HostConfigBody
            {
                Mounts = mounts,
                ReadonlyRootfs = true,
                CapDrop = ["ALL"],
                SecurityOpt = ["no-new-privileges:true"],
                Privileged = false,
                Init = true,
                PidsLimit = settings.PidsLimit,
                Memory = settings.MemoryBytes,
                MemorySwap = settings.MemoryBytes,
                NanoCpus = (long)(settings.Cpus * 1_000_000_000),
                NetworkMode = run.Network == SandboxNetworks.None ? "none" : open ? settings.OpenNetwork : settings.Networks[run.Network],
                Tmpfs = new Dictionary<string, string> { ["/tmp"] = "rw,size=1g,mode=1777" },
            },
        };
    }
}

public sealed record MountSpec(
    [property: JsonPropertyName("Type")] string Type,
    [property: JsonPropertyName("Source")] string Source,
    [property: JsonPropertyName("Target")] string Target,
    [property: JsonPropertyName("ReadOnly")] bool ReadOnly,
    [property: JsonPropertyName("VolumeOptions")] VolumeOptionsSpec? VolumeOptions = null);

public sealed record VolumeOptionsSpec([property: JsonPropertyName("NoCopy")] bool NoCopy);

public sealed class CreateContainerBody
{
    public required string Image { get; init; }
    public required IReadOnlyList<string> Cmd { get; init; }
    public required string User { get; init; }
    public required string WorkingDir { get; init; }
    public required IReadOnlyList<string> Env { get; init; }
    public required IReadOnlyDictionary<string, string> Labels { get; init; }
    public required HostConfigBody HostConfig { get; init; }
}

public sealed class HostConfigBody
{
    public required IReadOnlyList<MountSpec> Mounts { get; init; }
    public bool ReadonlyRootfs { get; init; }
    public required IReadOnlyList<string> CapDrop { get; init; }
    public required IReadOnlyList<string> SecurityOpt { get; init; }
    public bool Privileged { get; init; }
    public bool Init { get; init; }
    public int PidsLimit { get; init; }
    public long Memory { get; init; }
    public long MemorySwap { get; init; }
    public long NanoCpus { get; init; }
    public required string NetworkMode { get; init; }
    public required IReadOnlyDictionary<string, string> Tmpfs { get; init; }
}
