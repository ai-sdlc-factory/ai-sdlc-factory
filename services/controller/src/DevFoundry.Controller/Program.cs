using DevFoundry.Controller;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var settings = WorkerSettings.FromConfiguration(builder.Configuration);

builder.Services
    .AddHostedTemporalWorker(
        settings.TemporalAddress, settings.TemporalNamespace, settings.TemporalTaskQueue)
    .AddWorkflow<ForgejoEventWorkflow>()
    .AddWorkflow<OpenCodeDemoWorkflow>();
// Registered after the worker, so it stops first and the readiness file disappears before shutdown.
builder.Services.AddHostedService<ReadinessFile>();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(25));

await builder.Build().RunAsync();
