namespace DevFoundry.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Json(new { status = "ok" })).WithTags("Health");
        app.MapGet("/health/ready", async (ITemporalGateway gateway, CancellationToken cancellationToken) =>
        {
            try
            {
                await gateway.CheckReadyAsync(cancellationToken);
            }
            catch (TemporalUnavailableException)
            {
                return Responses.Detail(503, "Temporal is unavailable");
            }
            return Results.Json(new { status = "ready" });
        }).WithTags("Health");
        return app;
    }
}
