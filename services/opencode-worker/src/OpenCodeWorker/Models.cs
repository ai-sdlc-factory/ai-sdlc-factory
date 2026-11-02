using System.Text.Json.Serialization;

namespace OpenCodeWorker;

/// <summary>Activity input; the JSON names are the contract with the controller worker.</summary>
public sealed record OpenCodeTask(
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("activity_task_queue")] string? ActivityTaskQueue = null);

public sealed record OpenCodeResult(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("artifacts")] IReadOnlyDictionary<string, string> Artifacts);
