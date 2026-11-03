using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevFoundry.Contracts;

// The JSON names below are the wire contract between services and are stored in Temporal
// workflow histories. Changing one is a breaking change for running and replayed workflows.

/// <summary>Workflow input: a verified Forgejo webhook delivery, sent by the API to the controller.</summary>
public sealed record ForgejoEvent(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("payload")] JsonElement Payload);

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
    [property: JsonPropertyName("needs_model_auth")] bool NeedsModelAuth = false);

public sealed record SandboxRunResult(
    [property: JsonPropertyName("exit_code")] long ExitCode,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stderr")] string Stderr,
    [property: JsonPropertyName("output_truncated")] bool OutputTruncated);

/// <summary>Names shared by the launcher and the workflows that call it.</summary>
public static class SandboxNames
{
    public const string ActivityName = "sandbox_run";
    public const string DefaultTaskQueue = "sandbox";
}
