using DevFoundry.Controller;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var settings = WorkerSettings.FromConfiguration(builder.Configuration);

builder.Services
    .AddHostedTemporalWorker(
        settings.TemporalAddress, settings.TemporalNamespace, settings.TemporalTaskQueue)
    .AddScopedActivities<ForgejoActivities>()
    .AddScopedActivities<WorkspaceActivities>()
    .AddWorkflow<ForgejoEventWorkflow>()
    .AddWorkflow<FeatureWorkflow>()
    .AddWorkflow<BugWorkflow>()
    .AddWorkflow<IncidentWorkflow>()
    .AddWorkflow<PullRequestRevisionWorkflow>();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new GitWorkspace(settings));
builder.Services.AddSingleton(new ForgejoClient(
    new HttpClient { BaseAddress = new Uri(settings.ForgejoUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) },
    settings));
// Registered after the worker, so it stops first and the readiness file disappears before shutdown.
builder.Services.AddHostedService<ReadinessFile>();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(25));

await builder.Build().RunAsync();
