using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiSdlcFactory.Contracts;

// The JSON names below are the wire contract between services and are stored in Temporal
// workflow histories. Changing one is a breaking change for running and replayed workflows.

/// <summary>
/// Workflow input: a verified webhook delivery from a git host, sent by the API to the controller. The payload is the host's own JSON, kept as it came (the router reads it
/// through the host's <see cref="IWebhookAdapter"/>). <see cref="Host"/> is the name of the host the delivery came from (the end of its webhook route) and
/// <see cref="Adapter"/> the kind of host it is, which picks the adapter; deliveries written before there were hosts read as Forgejo's.
/// </summary>
public sealed record WebhookDelivery(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("host")] string Host = RepoNames.DefaultHost,
    [property: JsonPropertyName("adapter")] string Adapter = ForgejoWebhookAdapter.TypeName);

/// <summary>
/// Asks the sandbox launcher to run one command in a disposable container. The launcher decides
/// what the container may do; this request can only choose among options it already allows.
/// </summary>
public sealed record SandboxRunRequest(
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("workspace")] string Workspace,
    [property: JsonPropertyName("workspace_writable")] bool WorkspaceWritable,
    [property: JsonPropertyName("command")] IReadOnlyList<string> Command,
    [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string>? Env = null,
    [property: JsonPropertyName("needs_model_auth")] bool NeedsModelAuth = false,
    [property: JsonPropertyName("network")] string Network = SandboxNetworks.None,
    [property: JsonPropertyName("role")] string? Role = null);

public sealed record SandboxRunResult(
    [property: JsonPropertyName("exit_code")] long ExitCode,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stderr")] string Stderr,
    [property: JsonPropertyName("output_truncated")] bool OutputTruncated);

/// <summary>
/// Asks the launcher to build the sandbox image a repository describes in <c>.factory/Dockerfile</c>. The launcher refuses unless the
/// operator switched repository images on, accepts only a Dockerfile that starts from one of the factory's own images and uses a short list of
/// instructions, and builds it with a memory and time limit. The answer is an image alias for later <see cref="SandboxRunRequest"/>s.
/// </summary>
public sealed record SandboxBuildRequest(
    [property: JsonPropertyName("dockerfile")] string Dockerfile,
    [property: JsonPropertyName("files")] IReadOnlyDictionary<string, string>? Files = null,
    [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds = 1800);

/// <summary>
/// The image a build produced (or found already built: the name depends on the content, so an unchanged Dockerfile is built once).
/// <see cref="Image"/> is the alias to put in <see cref="SandboxRunRequest.Image"/>.
/// </summary>
public sealed record SandboxBuildResult(
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("built")] bool Built,
    [property: JsonPropertyName("seconds")] double Seconds,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("log_tail")] string LogTail);

/// <summary>
/// The network a sandbox may use. Each name is an egress profile: a list of hosts the egress proxy lets
/// that sandbox reach (see src/egress-proxy/profiles.json). A run that asks for nothing gets no network at all.
/// </summary>
public static class SandboxNetworks
{
    /// <summary>No network interface at all.</summary>
    public const string None = "none";

    /// <summary>The model provider only: for agents that read code and answer.</summary>
    public const string Model = "model";

    /// <summary>Package registries only: for the project's own checks (restore, install).</summary>
    public const string Packages = "packages";

    /// <summary>The model provider and package registries: for the developer.</summary>
    public const string Developer = "developer";
}

/// <summary>Names shared by the launcher and the workflows that call it.</summary>
public static class SandboxNames
{
    public const string ActivityName = "sandbox_run";
    public const string BuildActivityName = "sandbox_build_image";
    public const string DefaultTaskQueue = "sandbox";
}
