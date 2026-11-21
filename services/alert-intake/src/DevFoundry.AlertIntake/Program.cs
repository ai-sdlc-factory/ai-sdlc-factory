using DevFoundry.AlertIntake;

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
builder.Services.AddSingleton(services => IntakeSettings.FromConfiguration(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(services =>
{
    var settings = services.GetRequiredService<IntakeSettings>();
    return RouteTable.Load(settings.MappingFile, settings.Hosts.Select(host => host.Name).ToHashSet());
});
builder.Services.AddSingleton(services =>
    new RateLimiter(services.GetRequiredService<IntakeSettings>().MaxTicketsPerHour, TimeProvider.System));
// One client per git host (long-lived, a few at most), behind one that sends each call to the host the route names.
builder.Services.AddSingleton<IForgejo>(services =>
    new HostsClient(services.GetRequiredService<IntakeSettings>().Hosts.ToDictionary(
        host => host.Name,
        host =>
        {
            var client = new HttpClient { BaseAddress = new Uri(host.Url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };
            if (host.Token.Length > 0)
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", host.Token);
            }
            return (IForgejo)new ForgejoClient(client);
        })));
builder.Services.AddHttpClient<LokiClient>((services, client) =>
{
    var settings = services.GetRequiredService<IntakeSettings>();
    if (settings.LokiUrl.Length > 0)
    {
        client.BaseAddress = new Uri(settings.LokiUrl.TrimEnd('/') + "/");
    }
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddTransient<ILogSource>(services =>
    services.GetRequiredService<IntakeSettings>().LokiUrl.Length == 0 ? new NoLogs() : services.GetRequiredService<LokiClient>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IntakeMetrics>();
builder.Services.AddTransient<AlertProcessor>();

var app = builder.Build();
// Fail at startup, not on the first alert, when a setting is missing or the routes cannot be read.
_ = app.Services.GetRequiredService<IntakeSettings>();
_ = app.Services.GetRequiredService<RouteTable>();
app.MapIntakeEndpoints();
app.Run();
return 0;

public partial class Program;
