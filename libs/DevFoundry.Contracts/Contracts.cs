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
/// Activity input sent by the controller to the OpenCode worker. The worker validates the
/// fields itself because the task crosses a trust boundary, so they are nullable here.
/// </summary>
public sealed record OpenCodeTask(
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("activity_task_queue")] string? ActivityTaskQueue = null);
