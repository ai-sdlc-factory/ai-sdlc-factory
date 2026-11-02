using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevFoundry.Api;
using Temporalio.Client;

// Container healthcheck: the runtime image has no curl, so the app probes itself.
if (args.Contains("--healthcheck"))
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await http.GetAsync("http://127.0.0.1:8000/health/ready");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(sp => ApiSettings.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
// Lazy connection: the API starts even if Temporal is briefly down, and readiness reports it.
builder.Services.AddSingleton<ITemporalClient>(sp =>
{
    var settings = sp.GetRequiredService<ApiSettings>();
    return TemporalClient.CreateLazy(
        new TemporalClientConnectOptions(settings.TemporalAddress)
        {
            Namespace = settings.TemporalNamespace,
        });
});
builder.Services.AddSingleton<ITemporalGateway, TemporalGateway>();
builder.Services.AddOpenApi();

var app = builder.Build();
// Fail at startup, not on the first delivery, when the secret is missing or too short.
_ = app.Services.GetRequiredService<ApiSettings>();

app.MapOpenApi();
app.MapGet("/health/live", () => Results.Json(new { status = "ok" })).WithTags("Health");
app.MapGet("/health/ready", async (ITemporalGateway gateway, CancellationToken cancellationToken) =>
{
    try
    {
        await gateway.CheckReadyAsync(cancellationToken);
    }
    catch (TemporalUnavailableException)
    {
        return Detail(503, "Temporal is unavailable");
    }
    return Results.Json(new { status = "ready" });
}).WithTags("Health");

app.MapPost("/webhooks/forgejo", async (
    HttpRequest request,
    ITemporalGateway gateway,
    ApiSettings settings,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var signature = request.Headers["X-Forgejo-Signature"].ToString();
    if (!Patterns.Signature().IsMatch(signature))
    {
        return Detail(401, "Missing or invalid webhook signature");
    }

    if (request.ContentLength > settings.MaxWebhookBytes)
    {
        return Detail(413, "Webhook payload is too large");
    }
    // Count streamed bytes too: Content-Length is absent for chunked requests.
    using var buffer = new MemoryStream();
    var chunk = new byte[8192];
    int read;
    while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
    {
        if (buffer.Length + read > settings.MaxWebhookBytes)
        {
            return Detail(413, "Webhook payload is too large");
        }
        buffer.Write(chunk, 0, read);
    }
    var body = buffer.ToArray();

    var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.ForgejoWebhookSecret), body);
    if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature)))
    {
        return Detail(401, "Missing or invalid webhook signature");
    }

    var contentType = (request.ContentType ?? "").Split(';', 2)[0].Trim();
    if (!contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
    {
        return Detail(415, "Expected application/json");
    }

    var eventName = request.Headers["X-Forgejo-Event"].ToString();
    if (!Patterns.EventName().IsMatch(eventName))
    {
        return Detail(400, "Missing or invalid X-Forgejo-Event");
    }
    if (!Guid.TryParse(request.Headers["X-Forgejo-Delivery"].ToString(), out var delivery))
    {
        return Detail(400, "Missing or invalid X-Forgejo-Delivery");
    }
    var deliveryId = delivery.ToString();

    JsonDocument document;
    try
    {
        document = JsonDocument.Parse(body);
    }
    catch (JsonException)
    {
        return Detail(400, "Expected a valid JSON object");
    }
    using (document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return Detail(400, "Expected a JSON object");
        }

        var workflowId = $"forgejo-{deliveryId}";
        bool started;
        try
        {
            started = await gateway.StartForgejoWorkflowAsync(
                workflowId,
                new ForgejoEnvelope(deliveryId, eventName, document.RootElement),
                cancellationToken);
        }
        catch (TemporalUnavailableException)
        {
            logger.LogWarning("Temporal did not acknowledge webhook delivery {DeliveryId}", deliveryId);
            return Detail(503, "Temporal is unavailable; retry this delivery");
        }

        var receipt = new WebhookReceipt(started ? "accepted" : "duplicate", workflowId, deliveryId, eventName);
        return Results.Json(receipt, statusCode: started ? 202 : 200);
    }
})
.WithTags("Webhooks")
.WithSummary("Verify a Forgejo delivery and durably start its receipt workflow");

app.Run();
return 0;

static IResult Detail(int status, string detail) => Results.Json(new { detail }, statusCode: status);

public partial class Program;

internal static partial class Patterns
{
    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z")]
    public static partial Regex Signature();

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,63}\z")]
    public static partial Regex EventName();
}
