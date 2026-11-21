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
            .AddScopedActivities<GitHostActivities>()
            .AddScopedActivities<WorkspaceActivities>()
            .AddWorkflow<RepoEventWorkflow>()
            .AddWorkflow<FeatureWorkflow>()
            .AddWorkflow<BugWorkflow>()
            .AddWorkflow<IncidentWorkflow>()
            .AddWorkflow<PlanWorkflow>()
            .AddWorkflow<PullRequestRevisionWorkflow>()
            .AddWorkflow<PullRequestUpdateWorkflow>()
            .AddWorkflow<PullRequestReviewWorkflow>()
            .AddWorkflow<DocsWorkflow>()
            .AddWorkflow<AskWorkflow>()
            .AddWorkflow<SetupWorkflow>()
            .AddWorkflow<ImprovementWorkflow>()
            .AddWorkflow<QaWorkflow>();

        // The hosted worker keeps its client to itself, so the janitor gets one of its own (it connects when first used).
        services.AddTemporalClient(settings.TemporalAddress, settings.TemporalNamespace);

        services.AddSingleton(settings);
        services.AddSingleton(new GitWorkspace(settings));
        // The git hosts there are (the default one and the extra ones), behind one port that sends each call to the host its owner belongs to. The rest of the controller asks for IGitHost, never for a class.
        services.AddSingleton<IGitHost>(sp => new GitHostRouter(settings.Hosts.ToDictionary(
            host => host.Name,
            host =>
            {
                var http = new HttpClient { BaseAddress = new Uri(host.ApiBase + "/"), Timeout = TimeSpan.FromSeconds(20) };
                return host.Type == GitHubWebhookAdapter.TypeName ? (IGitHost)new GitHubClient(http, host.Token) : new ForgejoClient(http, host.Token);
            })));
        services.AddSingleton<IWorkspaceStore>(sp => sp.GetRequiredService<GitWorkspace>());
        services.AddSingleton<IRunningWorkflows>(sp => new TemporalRunningWorkflows(sp.GetRequiredService<ITemporalClient>()));
        services.AddSingleton<IJanitorGitHost>(sp => sp.GetRequiredService<IGitHost>());
        services.AddSingleton<JanitorRunner>();
        services.AddHostedService<JanitorService>();
        services.AddHostedService<ImprovementService>();
        services.AddHostedService<AgentFolderCheck>();
        // Registered after the worker, so it stops first and the readiness file disappears before shutdown.
        services.AddHostedService<ReadinessFile>();
        return services;
    }
}
