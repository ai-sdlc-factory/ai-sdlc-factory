using System.Text.Json;

namespace DevFoundry.Api;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/forgejo", ReceiveForgejoAsync)
            .WithTags("Webhooks")
            .WithSummary("Verify a Forgejo delivery and durably start its receipt workflow");
        return app;
    }

    // The order of checks is deliberate: nothing is parsed before the signature is verified.
    private static async Task<IResult> ReceiveForgejoAsync(
        HttpRequest request,
        ITemporalGateway gateway,
        ApiSettings settings,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        var signature = request.Headers["X-Forgejo-Signature"].ToString();
        if (!WebhookVerifier.IsWellFormedSignature(signature))
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

        if (!WebhookVerifier.IsValidSignature(settings.ForgejoWebhookSecret, body, signature))
        {
            return Responses.Detail(401, "Missing or invalid webhook signature");
        }

        var contentType = (request.ContentType ?? "").Split(';', 2)[0].Trim();
        if (!contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return Responses.Detail(415, "Expected application/json");
        }

        var eventName = request.Headers["X-Forgejo-Event"].ToString();
        if (!Patterns.EventName().IsMatch(eventName))
        {
            return Responses.Detail(400, "Missing or invalid X-Forgejo-Event");
        }
        if (!Guid.TryParse(request.Headers["X-Forgejo-Delivery"].ToString(), out var delivery))
        {
            return Responses.Detail(400, "Missing or invalid X-Forgejo-Delivery");
        }
        var deliveryId = delivery.ToString();

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

            var workflowId = $"forgejo-{deliveryId}";
            bool started;
            try
            {
                started = await gateway.StartForgejoWorkflowAsync(
                    workflowId,
                    new ForgejoEvent(deliveryId, eventName, document.RootElement),
                    cancellationToken);
            }
            catch (TemporalUnavailableException)
            {
                logger.LogWarning("Temporal did not acknowledge webhook delivery {DeliveryId}", deliveryId);
                return Responses.Detail(503, "Temporal is unavailable; retry this delivery");
            }

            var receipt = new WebhookReceipt(started ? "accepted" : "duplicate", workflowId, deliveryId, eventName);
            return Results.Json(receipt, statusCode: started ? 202 : 200);
        }
    }
}
