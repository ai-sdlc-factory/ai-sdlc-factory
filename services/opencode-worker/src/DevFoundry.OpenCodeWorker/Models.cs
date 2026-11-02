using System.Text.Json.Serialization;

namespace DevFoundry.OpenCodeWorker;

public sealed record OpenCodeResult(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("artifacts")] IReadOnlyDictionary<string, string> Artifacts);
