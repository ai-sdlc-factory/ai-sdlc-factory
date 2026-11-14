using Temporalio.Client;
using Temporalio.Extensions.Hosting;

namespace DevFoundry.Controller;

/// <summary>
/// Everything the controller registers, in one place so a test can build the same container and prove that every service can be created
/// (a missing registration otherwise only shows up as a crash when the container starts).
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddFactoryController(this IServiceCollection services, WorkerSettings settings)
    {
        services
            .AddHostedTemporalWorker(settings.TemporalAddress, settings.TemporalNamespace, settings.TemporalTaskQueue)
            .AddScopedActivities<ForgejoActivities>()
            .AddScopedActivities<WorkspaceActivities>()
            .AddWorkflow<ForgejoEventWorkflow>()
            .AddWorkflow<FeatureWorkflow>()
            .AddWorkflow<BugWorkflow>()
            .AddWorkflow<IncidentWorkflow>()
            .AddWorkflow<PlanWorkflow>()
            .AddWorkflow<PullRequestRevisionWorkflow>()
            .AddWorkflow<PullRequestUpdateWorkflow>()
            .AddWorkflow<QaWorkflow>();

        // The hosted worker keeps its client to itself, so the janitor gets one of its own (it connects when first used).
        services.AddTemporalClient(settings.TemporalAddress, settings.TemporalNamespace);

        services.AddSingleton(settings);
        services.AddSingleton(new GitWorkspace(settings));
        services.AddSingleton(sp => new ForgejoClient(
            new HttpClient { BaseAddress = new Uri(settings.ForgejoUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) },
            settings));
        services.AddSingleton<IWorkspaceStore>(sp => sp.GetRequiredService<GitWorkspace>());
        services.AddSingleton<IRunningWorkflows>(sp => new TemporalRunningWorkflows(sp.GetRequiredService<ITemporalClient>()));
        services.AddSingleton<IJanitorForgejo>(sp => sp.GetRequiredService<ForgejoClient>());
        services.AddSingleton<JanitorRunner>();
        services.AddHostedService<JanitorService>();
        // Registered after the worker, so it stops first and the readiness file disappears before shutdown.
        services.AddHostedService<ReadinessFile>();
        return services;
    }
}
