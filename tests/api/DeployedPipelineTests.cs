using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Temporalio.Client;

namespace AiSdlcFactory.Api.Tests;

/// <summary>
/// Exercises the separately deployed api and controller over HTTP and Temporal: one signed delivery for every git host of <c>FACTORY_HOSTS</c>. It needs a running stack, so it skips itself
/// unless <c>API_TEST_URL</c> is set; <c>docker compose --profile test run --rm --no-deps integration-tests</c> (in <c>deploy/</c>) runs it, and only it, with the settings of the stack.
/// </summary>
[Trait("Category", "Deployed")]
public sealed class DeployedPipelineTests
{
    private static string Env(string name, string? fallback = null) =>
        Environment.GetEnvironmentVariable(name) ?? fallback ?? throw new InvalidOperationException($"Set {name}.");

    private sealed record Host(string Name, string Type, string Secret);

    private static List<Host> Hosts()
    {
        using var hosts = JsonDocument.Parse(Env("FACTORY_HOSTS"));
        return hosts.RootElement.EnumerateArray()
            .Select(item => new Host(
                item.GetProperty("name").GetString()!,
                item.TryGetProperty("type", out var type) ? type.GetString()! : "forgejo",
                item.GetProperty("webhook_secret").GetString()!))
            .ToList();
    }

    /// <summary>A delivery as that kind of host sends it: its headers, and its signature over the exact bytes.</summary>
    private static HttpRequestMessage Request(Host host, byte[] body, string deliveryId, string signedWith)
    {
        var mac = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(host.Secret), signedWith == "" ? body : Encoding.UTF8.GetBytes(signedWith)));
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{host.Name}") { Content = new ByteArrayContent(body) };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        if (host.Type == "github")
        {
            request.Headers.Add("X-GitHub-Event", "issues");
            request.Headers.Add("X-GitHub-Delivery", deliveryId);
            request.Headers.Add("X-Hub-Signature-256", "sha256=" + mac);
        }
        else
        {
            request.Headers.Add("X-Forgejo-Event", "issues");
            request.Headers.Add("X-Forgejo-Delivery", deliveryId);
            request.Headers.Add("X-Forgejo-Signature", mac);
        }
        return request;
    }

    [SkippableFact]
    public async Task A_signed_webhook_of_each_host_completes_once_in_temporal()
    {
        Skip.If(Environment.GetEnvironmentVariable("API_TEST_URL") is null, "no deployed stack to test (API_TEST_URL is not set)");

        var temporal = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(Env("TEMPORAL_TEST_ADDRESS"))
        {
            Namespace = Env("TEMPORAL_TEST_NAMESPACE", "default"),
        });
        using var api = new HttpClient { BaseAddress = new Uri(Env("API_TEST_URL")), Timeout = TimeSpan.FromSeconds(10) };
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/health/ready")).StatusCode);

        var hosts = Hosts();
        Assert.NotEmpty(hosts);
        foreach (var host in hosts)
        {
            var body = Encoding.UTF8.GetBytes("""{"repository":{"full_name":"test/controller"},"action":"opened"}""");
            var deliveryId = Guid.NewGuid().ToString();

            var response = await api.SendAsync(Request(host, body, deliveryId, ""));
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{host.Name}: {await response.Content.ReadAsStringAsync()}");
            var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
            var workflowId = receipt.GetProperty("workflow_id").GetString()!;
            Assert.Equal($"{host.Name}-{deliveryId}", workflowId);

            var result = await temporal.GetWorkflowHandle(workflowId).GetResultAsync<JsonElement>().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(deliveryId, result.GetProperty("delivery_id").GetString());
            Assert.Equal("issues", result.GetProperty("event").GetString());
            // The receipt names the repository as the host does; which host it was is in the workflow id.
            Assert.Equal("test/controller", result.GetProperty("repository").GetString());
            Assert.Equal("opened", result.GetProperty("action").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("ref").ValueKind);

            var duplicate = await api.SendAsync(Request(host, body, deliveryId, ""));
            Assert.True(duplicate.StatusCode == HttpStatusCode.OK, $"{host.Name}: {await duplicate.Content.ReadAsStringAsync()}");
            var duplicateReceipt = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("duplicate", duplicateReceipt.GetProperty("status").GetString());
            Assert.Equal(workflowId, duplicateReceipt.GetProperty("workflow_id").GetString());

            // The signature of the original bytes on a body that was changed on the way.
            var tampered = Request(host, [.. body, (byte)' '], deliveryId, Encoding.UTF8.GetString(body));
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.SendAsync(tampered)).StatusCode);
        }
    }
}
