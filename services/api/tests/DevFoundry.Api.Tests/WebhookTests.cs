using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevFoundry.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DevFoundry.Api.Tests;

public sealed class FakeGateway : ITemporalGateway
{
    public List<(string WorkflowId, ForgejoEnvelope Envelope)> Started { get; } = [];
    public Exception? StartFailure { get; set; }
    public bool StartReturns { get; set; } = true;
    public Exception? ReadyFailure { get; set; }

    public Task<bool> StartForgejoWorkflowAsync(
        string workflowId, ForgejoEnvelope envelope, CancellationToken cancellationToken)
    {
        if (StartFailure is not null)
        {
            throw StartFailure;
        }
        // The request owns the parsed document and disposes it once the response is sent.
        Started.Add((workflowId, envelope with { Payload = envelope.Payload.Clone() }));
        return Task.FromResult(StartReturns);
    }

    public Task CheckReadyAsync(CancellationToken cancellationToken) =>
        ReadyFailure is null ? Task.CompletedTask : throw ReadyFailure;
}

/// <summary>Request body without a known length, so it is sent chunked.</summary>
public sealed class ChunkedContent(params byte[][] chunks) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        foreach (var chunk in chunks)
        {
            await stream.WriteAsync(chunk);
            await stream.FlushAsync();
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}

public sealed class WebhookTests
{
    private const string Secret = "controller-test-secret-never-used-outside-tests";
    private static readonly byte[] Body =
        """{ "repository": {"full_name": "owner/repo"}, "ref": "refs/heads/main" }"""u8.ToArray();

    private readonly FakeGateway gateway = new();

    private HttpClient CreateClient(int maxBytes = 1_048_576) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new ApiSettings
                {
                    ForgejoWebhookSecret = Secret,
                    MaxWebhookBytes = maxBytes,
                });
                services.AddSingleton<ITemporalGateway>(gateway);
            })).CreateClient();

    private static string Sign(byte[] body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));

    private static HttpRequestMessage Request(
        byte[] body,
        HttpContent? content = null,
        string? signature = null,
        string? delivery = null,
        string eventName = "push",
        string contentType = "application/json")
    {
        content ??= new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/forgejo") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Forgejo-Event", eventName);
        request.Headers.TryAddWithoutValidation("X-Forgejo-Delivery", delivery ?? Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("X-Forgejo-Signature", signature ?? Sign(body));
        return request;
    }

    [Fact]
    public async Task Signed_event_is_durable_before_acceptance()
    {
        var delivery = Guid.NewGuid().ToString();
        var response = await CreateClient().SendAsync(Request(Body, delivery: delivery));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("accepted", receipt.GetProperty("status").GetString());
        Assert.Equal($"forgejo-{delivery}", receipt.GetProperty("workflow_id").GetString());
        var (workflowId, envelope) = Assert.Single(gateway.Started);
        Assert.Equal($"forgejo-{delivery}", workflowId);
        Assert.Equal("push", envelope.Event);
        Assert.Equal(delivery, envelope.DeliveryId);
        Assert.Equal("owner/repo", envelope.Payload.GetProperty("repository").GetProperty("full_name").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Invalid_signature_never_starts_work(string signature)
    {
        var response = await CreateClient().SendAsync(Request(Body, signature: signature));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(gateway.Started);
    }

    [Fact]
    public async Task Missing_signature_never_starts_work()
    {
        var request = Request(Body);
        request.Headers.Remove("X-Forgejo-Signature");
        var response = await CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(gateway.Started);
    }

    [Fact]
    public async Task Signature_uses_exact_original_bytes()
    {
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Body).Replace("owner/repo", "owner/evil"));
        var response = await CreateClient().SendAsync(Request(changed, signature: Sign(Body)));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(gateway.Started);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"value": NaN}""")]
    public async Task Invalid_json_never_starts_work(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var response = await CreateClient().SendAsync(Request(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gateway.Started);
    }

    [Fact]
    public async Task Invalid_utf8_never_starts_work()
    {
        byte[] body = [0xff];
        var response = await CreateClient().SendAsync(Request(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gateway.Started);
    }

    [Theory]
    [InlineData("delivery", "")]
    [InlineData("delivery", "invalid")]
    [InlineData("event", "")]
    [InlineData("event", "../unexpected")]
    public async Task Invalid_headers_never_start_work(string header, string value)
    {
        var request = header == "delivery"
            ? Request(Body, delivery: value)
            : Request(Body, eventName: value);
        var response = await CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gateway.Started);
    }

    [Fact]
    public async Task Non_json_content_type_is_rejected()
    {
        var response = await CreateClient().SendAsync(
            Request(Body, contentType: "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(gateway.Started);
    }

    [Fact]
    public async Task Streamed_body_limit_does_not_depend_on_content_length()
    {
        var content = new ChunkedContent(Body[..10], Body[10..]);
        var response = await CreateClient(maxBytes: 8).SendAsync(Request(Body, content));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(gateway.Started);
    }

    [Fact]
    public async Task Duplicate_delivery_is_acknowledged()
    {
        gateway.StartReturns = false;
        var response = await CreateClient().SendAsync(Request(Body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("duplicate", receipt.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Temporal_failure_does_not_acknowledge_delivery()
    {
        gateway.StartFailure = new TemporalUnavailableException(new TimeoutException());
        var response = await CreateClient().SendAsync(Request(Body));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("retry", error.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Health_checks_report_temporal_availability()
    {
        var client = CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        gateway.ReadyFailure = new TemporalUnavailableException(new TimeoutException());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_available()
    {
        var response = await CreateClient().GetAsync("/openapi/v1.json");
        Assert.Contains("/webhooks/forgejo", await response.Content.ReadAsStringAsync());
    }
}

public sealed class SettingsTests
{
    [Fact]
    public void Secret_must_be_configured_without_exposing_it()
    {
        const string invalid = "test-secret-too-short";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FORGEJO_WEBHOOK_SECRET"] = invalid })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => { ApiSettings.FromConfiguration(configuration); });
        Assert.DoesNotContain(invalid, error.Message);
        Assert.Contains("FORGEJO_WEBHOOK_SECRET", error.Message);
    }
}
