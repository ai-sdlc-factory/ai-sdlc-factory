using System.Text;
using System.Text.Json;
using FactoryController.Worker;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace FactoryController.Worker.Tests;

public sealed class ContractTests
{
    private const string DeliveryId = "12345678-1234-1234-1234-123456789abc";

    private static Payload JsonPayload(string json) => new()
    {
        Metadata = { ["encoding"] = ByteString.CopyFromUtf8("json/plain") },
        Data = ByteString.CopyFromUtf8(json),
    };

    [Fact]
    public async Task Plain_json_from_the_api_is_decoded_and_processed()
    {
        // The API sends plain JSON; this must keep matching histories created by earlier versions.
        var payload = JsonPayload(
            $$$"""{"delivery_id":"{{{DeliveryId}}}","event":"issues","payload":{"repository":{"full_name":"test/contract"},"action":"opened"}}""");

        var input = DataConverter.Default.PayloadConverter.ToValue<ForgejoEvent>(payload);
        var receipt = await new ForgejoEventWorkflow().RunAsync(input);

        Assert.Equal(new ForgejoReceipt(DeliveryId, "issues", "test/contract", "opened", null), receipt);
    }

    [Fact]
    public async Task Receipt_serializes_with_the_existing_field_names()
    {
        var input = new ForgejoEvent(
            DeliveryId, "push", JsonDocument.Parse("""{"ref":"refs/heads/main"}""").RootElement);
        var receipt = await new ForgejoEventWorkflow().RunAsync(input);

        var encoded = DataConverter.Default.PayloadConverter.ToPayload(receipt);
        using var json = JsonDocument.Parse(encoded.Data.ToByteArray());
        Assert.Equal(
            ["delivery_id", "event", "repository", "action", "ref"],
            json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("repository").ValueKind);
        Assert.Equal("refs/heads/main", json.RootElement.GetProperty("ref").GetString());
    }

    [Fact]
    public async Task Unexpected_payload_shapes_produce_empty_fields()
    {
        var input = new ForgejoEvent(
            DeliveryId, "push", JsonDocument.Parse("""{"repository":"text","action":7}""").RootElement);
        var receipt = await new ForgejoEventWorkflow().RunAsync(input);

        Assert.Equal(new ForgejoReceipt(DeliveryId, "push", null, null, null), receipt);
    }

    [Fact]
    public void Demo_task_uses_the_snake_case_names_the_execution_worker_reads()
    {
        var task = new OpenCodeTask("task-1", "Do it.", "opencode-tasks");
        var encoded = DataConverter.Default.PayloadConverter.ToPayload(task);
        Assert.Equal(
            """{"task_id":"task-1","prompt":"Do it.","activity_task_queue":"opencode-tasks"}""",
            Encoding.UTF8.GetString(encoded.Data.ToByteArray()));
    }

    [Fact]
    public void Worker_settings_use_documented_defaults()
    {
        var settings = new WorkerSettings();
        Assert.Equal("default", settings.TemporalNamespace);
        Assert.Equal("factory-events", settings.TemporalTaskQueue);
    }
}
