using System.Text.Json.Serialization;

namespace AiSdlcFactory.Api;

public sealed record WebhookReceipt(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("workflow_id")] string WorkflowId,
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("event")] string Event);
