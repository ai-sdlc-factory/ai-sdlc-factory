using System.Text.Json;
using System.Text.Json.Serialization;

namespace FactoryController.Worker;

/// <summary>Workflow input sent by the controller API; the JSON names are the contract.</summary>
public sealed record ForgejoEvent(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("payload")] JsonElement Payload);

public sealed record ForgejoReceipt(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("repository")] string? Repository,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("ref")] string? Ref);

public sealed record OpenCodeTask(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("activity_task_queue")] string? ActivityTaskQueue);
