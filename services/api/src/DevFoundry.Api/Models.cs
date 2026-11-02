using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevFoundry.Api;

/// <summary>Workflow input; the JSON shape is the contract with the controller worker.</summary>
public sealed record ForgejoEnvelope(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("payload")] JsonElement Payload);

public sealed record WebhookReceipt(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("workflow_id")] string WorkflowId,
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("event")] string Event);
