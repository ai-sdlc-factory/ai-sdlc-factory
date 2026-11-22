using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using AiSdlcFactory.Controller;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace AiSdlcFactory.Controller.Tests;

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

        var input = DataConverter.Default.PayloadConverter.ToValue<WebhookDelivery>(payload);
        var receipt = await new RepoEventWorkflow().RunAsync(input);

        Assert.Equal(new EventReceipt(DeliveryId, "issues", "test/contract", "opened", null), receipt);
    }

    [Fact]
    public async Task A_delivery_from_a_host_without_an_adapter_fails_instead_of_being_guessed_at()
    {
        var input = new WebhookDelivery(DeliveryId, "issues", JsonDocument.Parse("{}").RootElement, Adapter: "nowhere");

        var error = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() => new RepoEventWorkflow().RunAsync(input));

        Assert.Contains("nowhere", error.Message);
    }

    [Fact]
    public void A_delivery_written_before_hosts_existed_reads_as_forgejos_and_a_new_one_says_its_host()
    {
        var old = DataConverter.Default.PayloadConverter.ToValue<WebhookDelivery>(JsonPayload($$$"""{"delivery_id":"{{{DeliveryId}}}","event":"issues","payload":{}}"""));
        var encoded = DataConverter.Default.PayloadConverter.ToPayload(old);
        using var json = JsonDocument.Parse(encoded.Data.ToByteArray());

        Assert.Equal(("forgejo", "forgejo"), (old.Host, old.Adapter));
        Assert.Equal(["delivery_id", "event", "payload", "host", "adapter"], json.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Receipt_serializes_with_the_existing_field_names()
    {
        var input = new WebhookDelivery(
            DeliveryId, "push", JsonDocument.Parse("""{"ref":"refs/heads/main"}""").RootElement);
        var receipt = await new RepoEventWorkflow().RunAsync(input);

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
        var input = new WebhookDelivery(
            DeliveryId, "push", JsonDocument.Parse("""{"repository":"text","action":7}""").RootElement);
        var receipt = await new RepoEventWorkflow().RunAsync(input);

        Assert.Equal(new EventReceipt(DeliveryId, "push", null, null, null), receipt);
    }

    [Theory]
    [InlineData("openai/gpt-6.1-sol")]
    [InlineData("anthropic/claude-sonnet-5-5")]
    [InlineData("ollama/llama3:8b")]
    [InlineData("openrouter/anthropic/claude-sonnet-5-5")]
    public void A_model_name_is_provider_slash_model(string name) => Assert.Equal(name, ModelNames.Read($" {name} "));

    [Theory]
    [InlineData("gpt-6.1-sol")]
    [InlineData("openai/")]
    [InlineData("/model")]
    [InlineData("openai/model name")]
    [InlineData("openai/model\"}")]
    [InlineData("OpenAI/model")]
    public void Anything_else_stops_the_start(string name) => Assert.Contains("FACTORY_MODEL", Assert.Throws<InvalidOperationException>(() => ModelNames.Read(name)).Message);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_model_setting_means_no_override(string? value) => Assert.Null(ModelNames.Read(value));

    [Fact]
    public void The_controller_reads_the_model_setting_and_rejects_a_bad_one()
    {
        WorkerSettings Load(string? value) => WorkerSettings.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(TestHosts.Setting).AddInMemoryCollection(new Dictionary<string, string?> { ["FACTORY_MODEL"] = value }).Build());

        Assert.Null(Load(null).Model);
        Assert.Equal("openai/other-model", Load("openai/other-model").Model);
        Assert.Throws<InvalidOperationException>(() => Load("other-model"));
    }

    [Fact]
    public void Worker_settings_use_documented_defaults()
    {
        var settings = new WorkerSettings();
        Assert.Equal("default", settings.TemporalNamespace);
        Assert.Equal("factory-events", settings.TemporalTaskQueue);
    }
}
