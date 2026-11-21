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
    return RouteTable.Load(settings.MappingFile, settings.Hosts.Select(host => host.Name).ToList());
});
builder.Services.AddSingleton(services =>
    new RateLimiter(services.GetRequiredService<IntakeSettings>().MaxTicketsPerHour, TimeProvider.System));
// One client per git host (long-lived, a few at most), of the kind the host is; a route names the host its tickets go to.
builder.Services.AddSingleton<ITicketHosts>(services =>
    new TicketHosts(services.GetRequiredService<IntakeSettings>().Hosts.ToDictionary(
        host => host.Name,
        host =>
        {
            var github = host.Type == HostList.GitHub;
            var client = new HttpClient { BaseAddress = new Uri((github ? GitHubClient.ApiBase(host.Url) : host.Url) + "/"), Timeout = TimeSpan.FromSeconds(20) };
            if (github)
            {
                GitHubClient.Prepare(client, host.Token);
                return (ITicketHost)new GitHubClient(client);
            }
            ForgejoClient.Prepare(client, host.Token);
            return new ForgejoClient(client);
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
// One memory of the tickets just opened, shared by every request (the processor itself is made per request).
builder.Services.AddSingleton<RecentTickets>();
builder.Services.AddTransient<AlertProcessor>();

var app = builder.Build();
// Fail at startup, not on the first alert, when a setting is missing or the routes cannot be read.
_ = app.Services.GetRequiredService<IntakeSettings>();
_ = app.Services.GetRequiredService<RouteTable>();
app.MapIntakeEndpoints();
app.Run();
return 0;

public partial class Program;
