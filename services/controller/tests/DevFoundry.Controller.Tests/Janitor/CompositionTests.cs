using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DevFoundry.Controller.Tests;

/// <summary>Builds the controller's real container, without starting the worker, so a missing registration fails here and not at deploy time.</summary>
public sealed class CompositionTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryController(new WorkerSettings { WorkspaceRoot = "/tmp/x", ForgejoToken = "t" });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
    }

    [Fact]
    public void The_janitor_and_everything_it_needs_can_be_created()
    {
        using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<JanitorRunner>());
        Assert.NotNull(provider.GetRequiredService<IRunningWorkflows>());
        Assert.NotNull(provider.GetRequiredService<IWorkspaceStore>());
        Assert.NotNull(provider.GetRequiredService<IJanitorGitHost>());
        Assert.NotNull(provider.GetRequiredService<Temporalio.Client.ITemporalClient>());
    }

    [Fact]
    public void Every_hosted_service_can_be_created()
    {
        using var provider = Build();

        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.Contains(hosted, service => service is JanitorService);
        Assert.Contains(hosted, service => service is ReadinessFile);
        Assert.True(hosted.Count >= 3); // the Temporal worker, the janitor and the readiness file
    }

    [Fact]
    public void Activities_and_workflows_are_registered_with_what_they_need()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ForgejoActivities>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<WorkspaceActivities>());
    }
}
