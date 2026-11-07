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
builder.Services.AddSingleton(services => RouteTable.Load(services.GetRequiredService<IntakeSettings>().MappingFile));
builder.Services.AddSingleton(services =>
    new RateLimiter(services.GetRequiredService<IntakeSettings>().MaxTicketsPerHour, TimeProvider.System));
builder.Services.AddHttpClient<IForgejo, ForgejoClient>((services, client) =>
{
    var settings = services.GetRequiredService<IntakeSettings>();
    client.BaseAddress = new Uri(settings.ForgejoUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(20);
    if (settings.ForgejoToken.Length > 0)
    {
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", settings.ForgejoToken);
    }
});
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
