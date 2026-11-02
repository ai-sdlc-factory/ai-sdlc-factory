using System.Text.Json.Serialization;

namespace DevFoundry.SandboxLauncher;

/// <summary>
/// Builds the Docker create-container body. All isolation settings are fixed here, in one place,
/// and nothing in a request can change them.
/// </summary>
public static class ContainerSpec
{
    public const string Label = "dev-foundry.sandbox";
    public const string WorkspaceTarget = "/workspace";
    public const string GlobalConfigTarget = "/etc/factory";
    public const string AuthTarget = "/auth";

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
            Labels = new Dictionary<string, string> { [Label] = "true" },
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
                NetworkMode = settings.Network,
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
