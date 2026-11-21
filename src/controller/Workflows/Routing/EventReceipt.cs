using System.Text.Json.Serialization;

namespace DevFoundry.Controller;

/// <summary>What a routed delivery leaves in Temporal: which delivery it was and what it was about (never its contents).</summary>
public sealed record EventReceipt(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("repository")] string? Repository,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("ref")] string? Ref);
