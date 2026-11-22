using AiSdlcFactory.Api;

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
builder.Services.AddApiSettings();
builder.Services.AddTemporal();
builder.Services.AddOpenApi();

var app = builder.Build();
// Fail at startup, not on the first delivery, when the secret is missing or too short.
_ = app.Services.GetRequiredService<ApiSettings>();

app.MapOpenApi();
app.MapHealthEndpoints();
app.MapWebhookEndpoints();

app.Run();
return 0;

public partial class Program;
