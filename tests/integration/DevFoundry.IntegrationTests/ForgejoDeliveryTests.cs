using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Temporalio.Client;

namespace DevFoundry.IntegrationTests;

/// <summary>Exercises the separately deployed API and controller over HTTP and Temporal.</summary>
public sealed class ForgejoDeliveryTests
{
    private static string Env(string name, string? fallback = null) =>
        Environment.GetEnvironmentVariable(name)
        ?? fallback
        ?? throw new InvalidOperationException($"Set {name}.");

    private static HttpRequestMessage Request(byte[] body, string deliveryId, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/forgejo")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        request.Headers.Add("X-Forgejo-Event", "issues");
        request.Headers.Add("X-Forgejo-Delivery", deliveryId);
        request.Headers.Add("X-Forgejo-Signature", signature);
        return request;
    }

    [Fact]
    public async Task Signed_webhook_completes_once_in_temporal()
    {
        var secret = Env("FORGEJO_WEBHOOK_SECRET");
        var body = Encoding.UTF8.GetBytes(
            """{"repository":{"full_name":"test/controller"},"action":"opened"}""");
        var deliveryId = Guid.NewGuid().ToString();
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

        var temporal = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions(Env("TEMPORAL_TEST_ADDRESS"))
            {
                Namespace = Env("TEMPORAL_TEST_NAMESPACE", "default"),
            });
        using var api = new HttpClient
        {
            BaseAddress = new Uri(Env("API_TEST_URL")),
            Timeout = TimeSpan.FromSeconds(10),
        };

        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/health/ready")).StatusCode);

        var response = await api.SendAsync(Request(body, deliveryId, signature));
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = receipt.GetProperty("workflow_id").GetString()!;
        Assert.Equal($"forgejo-{deliveryId}", workflowId);

        var result = await temporal.GetWorkflowHandle(workflowId)
            .GetResultAsync<JsonElement>()
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(deliveryId, result.GetProperty("delivery_id").GetString());
        Assert.Equal("issues", result.GetProperty("event").GetString());
        Assert.Equal("test/controller", result.GetProperty("repository").GetString());
        Assert.Equal("opened", result.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("ref").ValueKind);

        var duplicate = await api.SendAsync(Request(body, deliveryId, signature));
        Assert.True(duplicate.StatusCode == HttpStatusCode.OK, await duplicate.Content.ReadAsStringAsync());
        var duplicateReceipt = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("duplicate", duplicateReceipt.GetProperty("status").GetString());
        Assert.Equal(workflowId, duplicateReceipt.GetProperty("workflow_id").GetString());

        var tampered = await api.SendAsync(Request([.. body, (byte)' '], deliveryId, signature));
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);
    }
}
