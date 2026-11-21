using System.Text.Json;

namespace DevFoundry.Api;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // One route per host the factory takes webhooks from (every one listed in FACTORY_HOSTS), so a host that is not there is an ordinary 404 and the OpenAPI document lists each.
        var configured = app.ServiceProvider.GetRequiredService<ApiSettings>();
        foreach (var host in configured.Hosts)
        {
            var known = host;
            var adapter = WebhookAdapters.Find(known.Type) ?? throw new InvalidOperationException($"No webhook adapter for the host type \"{known.Type}\".");
            app.MapPost($"/webhooks/{known.Name}",
                    (HttpRequest request, ITemporalGateway gateway, ApiSettings settings, ILogger<Program> logger, CancellationToken cancellationToken) =>
                        ReceiveAsync(known.Name, adapter, request, gateway, settings, logger, cancellationToken))
                .WithTags("Webhooks")
                .WithSummary($"Verify a {known.Name} delivery and durably start its receipt workflow");
        }
        return app;
    }

    // The order of checks is deliberate: nothing is parsed before the signature is verified.
    private static async Task<IResult> ReceiveAsync(
        string host,
        IWebhookAdapter adapter,
        HttpRequest request,
        ITemporalGateway gateway,
        ApiSettings settings,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        // A host with an adapter but no secret here is not there: nothing about it is revealed beyond that.
        if (settings.WebhookSecretFor(host) is not { Length: > 0 } secret)
        {
            return Responses.Detail(404, "Unknown webhook host");
        }

        string Header(string name) => request.Headers[name].ToString();
        if (!adapter.HasWellFormedSignature(Header))
        {
            return Responses.Detail(401, "Missing or invalid webhook signature");
        }

        if (request.ContentLength > settings.MaxWebhookBytes)
        {
            return Responses.Detail(413, "Webhook payload is too large");
        }
        var body = await WebhookVerifier.ReadBoundedAsync(request.Body, settings.MaxWebhookBytes, cancellationToken);
        if (body is null)
        {
            return Responses.Detail(413, "Webhook payload is too large");
        }

        if (!adapter.IsValidSignature(Header, body, secret))
        {
            return Responses.Detail(401, "Missing or invalid webhook signature");
        }

        var contentType = (request.ContentType ?? "").Split(';', 2)[0].Trim();
        if (!contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return Responses.Detail(415, "Expected application/json");
        }

        if (adapter.EventName(Header) is not { } eventName)
        {
            return Responses.Detail(400, $"Missing or invalid {adapter.EventHeader}");
        }
        if (adapter.DeliveryId(Header) is not { } deliveryId)
        {
            return Responses.Detail(400, $"Missing or invalid {adapter.DeliveryHeader}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Responses.Detail(400, "Expected a valid JSON object");
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Responses.Detail(400, "Expected a JSON object");
            }

            var workflowId = $"{host}-{deliveryId}";
            bool started;
            try
            {
                started = await gateway.StartEventWorkflowAsync(
                    workflowId,
                    new WebhookDelivery(deliveryId, eventName, document.RootElement, host, adapter.Type),
                    cancellationToken);
            }
            catch (TemporalUnavailableException)
            {
                logger.LogWarning("Temporal did not acknowledge {Host} webhook delivery {DeliveryId}", host, deliveryId);
                return Responses.Detail(503, "Temporal is unavailable; retry this delivery");
            }

            // The event name is checked against a strict pattern above, so it is safe to log; the payload never is.
            logger.LogInformation("{Host} {Event} delivery {DeliveryId}: {Status}", host, eventName, deliveryId, started ? "accepted" : "duplicate");
            var receipt = new WebhookReceipt(started ? "accepted" : "duplicate", workflowId, deliveryId, eventName);
            return Results.Json(receipt, statusCode: started ? 202 : 200);
        }
    }
}
