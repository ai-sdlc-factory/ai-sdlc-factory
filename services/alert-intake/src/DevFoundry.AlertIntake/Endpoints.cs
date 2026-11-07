using System.Security.Cryptography;
using System.Text;

namespace DevFoundry.AlertIntake;

public static class Endpoints
{
    public static void MapIntakeEndpoints(this WebApplication app)
    {
        app.MapGet("/health/ready", (IntakeSettings settings, RouteTable routes) =>
            Results.Ok(new { status = "ok", dryRun = settings.DryRun, routes = routes.Count }));

        // Counters only, no text from alerts. Not published outside the Compose network; Prometheus scrapes it there.
        app.MapGet("/metrics", (IntakeMetrics metrics) => Results.Text(metrics.Render(), "text/plain; version=0.0.4"));

        app.MapPost("/alerts", async (HttpContext context, IntakeSettings settings, AlertProcessor processor, IntakeMetrics metrics) =>
        {
            if (!Authorized(context.Request.Headers.Authorization.ToString(), settings.Token))
            {
                metrics.Rejected("unauthorized");
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            if (context.Request.ContentLength > settings.MaxBodyBytes)
            {
                metrics.Rejected("too_large");
                return Results.Json(new { error = "body too large" }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var body = await ReadBodyAsync(context.Request.Body, settings.MaxBodyBytes, context.RequestAborted);
            if (body is null)
            {
                metrics.Rejected("too_large");
                return Results.Json(new { error = "body too large" }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            if (!AlertMessage.TryParse(body, out var message, out var error))
            {
                metrics.Rejected("invalid");
                return Results.BadRequest(new { error });
            }
            metrics.Notification(message!.Alerts!.Count);

            var outcomes = await processor.ProcessAsync(message!, context.RequestAborted);
            // A failure for any alert makes Alertmanager retry the notification; what already succeeded is a duplicate then, not a second ticket.
            var failed = outcomes.Any(outcome => outcome.Action == AlertOutcome.Failed);
            return Results.Json(new { outcomes }, statusCode: failed ? StatusCodes.Status502BadGateway : StatusCodes.Status200OK);
        });
    }

    /// <summary>Compares in constant time, so the check does not tell an attacker how much of a guess was right.</summary>
    public static bool Authorized(string header, string token)
    {
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(header[prefix.Length..].Trim()));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>The whole body as text, or null when it is longer than <paramref name="limit"/> bytes (also when the length was not announced).</summary>
    private static async Task<string?> ReadBodyAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                return null;
            }
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
