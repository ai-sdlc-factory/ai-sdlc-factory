using System.Text.Json;
using Temporalio.Workflows;

namespace FactoryController.Worker;

[Workflow("ForgejoEventWorkflow")]
public sealed class ForgejoEventWorkflow
{
    /// <summary>
    /// Preserves the input in workflow history and produces a small receipt.
    /// Factory actions will be added here once their behavior is defined.
    /// Webhook contents are data; this workflow executes no supplied commands.
    /// </summary>
    [WorkflowRun]
    public Task<ForgejoReceipt> RunAsync(ForgejoEvent input)
    {
        var payload = input.Payload;
        string? repository = null;
        if (TryGet(payload, "repository", out var repo) && TryGet(repo, "full_name", out var name))
        {
            repository = AsString(name);
        }

        TryGet(payload, "action", out var action);
        TryGet(payload, "ref", out var reference);
        return Task.FromResult(new ForgejoReceipt(
            input.DeliveryId, input.Event, repository, AsString(action), AsString(reference)));
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static string? AsString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
