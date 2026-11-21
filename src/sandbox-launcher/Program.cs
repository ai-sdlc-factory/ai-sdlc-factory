using DevFoundry.SandboxLauncher;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var settings = SandboxSettings.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<IDockerClient>(DockerClient.FromSocket(settings.DockerSocket));
builder.Services.AddSingleton(sp => new SandboxRunner(sp.GetRequiredService<IDockerClient>(), settings));
builder.Services.AddSingleton(sp => new RepoImageBuilder(sp.GetRequiredService<IDockerClient>(), settings));
// Registered before the worker, so it runs first: the worker does not survive starting while Temporal cannot be reached.
builder.Services.AddHostedService(sp => new WaitForTemporal(settings.TemporalAddress, sp.GetRequiredService<ILogger<WaitForTemporal>>()));
builder.Services
    .AddHostedTemporalWorker(settings.TemporalAddress, settings.TemporalNamespace, settings.TaskQueue)
    .ConfigureOptions(options => options.MaxConcurrentActivities = settings.MaxConcurrentRuns)
    .AddScopedActivities<SandboxActivities>();
builder.Services.AddHostedService<SandboxJanitor>();
builder.Services.AddSingleton(sp => new RepoImageCleaner(sp.GetRequiredService<IDockerClient>(), settings, sp.GetRequiredService<ILogger<RepoImageCleaner>>()));
builder.Services.AddHostedService<RepoImageJanitor>();
// Registered last, so it stops first and the readiness file disappears before shutdown.
builder.Services.AddHostedService<ReadinessFile>();
// Give a running sandbox time to finish or be killed and removed before the process exits.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(25));

await builder.Build().RunAsync();
