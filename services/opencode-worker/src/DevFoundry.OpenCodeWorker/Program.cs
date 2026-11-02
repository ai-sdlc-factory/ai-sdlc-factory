using DevFoundry.OpenCodeWorker;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var settings = Settings.FromConfiguration(builder.Configuration);

// `submit` runs the manual demo task instead of the activity worker.
if (args.FirstOrDefault() == "submit")
{
    await DemoSubmitter.RunAsync(settings);
    return;
}

builder.Services.AddSingleton(settings);
builder.Services
    .AddHostedTemporalWorker(
        settings.TemporalAddress, settings.TemporalNamespace, settings.OpenCodeTaskQueue)
    .AddSingletonActivities<OpenCodeActivities>()
    .ConfigureOptions(options => options.MaxConcurrentActivities = 1);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(25));

await builder.Build().RunAsync();
