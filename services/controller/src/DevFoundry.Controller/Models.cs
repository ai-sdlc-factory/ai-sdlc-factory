using System.Text.Json.Serialization;

namespace DevFoundry.Controller;

public sealed record ForgejoReceipt(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("repository")] string? Repository,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("ref")] string? Ref);
