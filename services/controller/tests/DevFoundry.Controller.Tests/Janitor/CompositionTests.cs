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
    public void Every_workflow_the_controller_has_is_registered_on_the_worker()
    {
        // A workflow the scenario harness registers but the real worker does not looks fine in every test and then fails in production ("not registered on this worker").
        using var provider = Build();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Temporalio.Extensions.Hosting.TemporalWorkerServiceOptions>>()
            .Get(new WorkerSettings().TemporalTaskQueue);

        var registered = options.Workflows.Select(workflow => workflow.Name).ToHashSet();
        var all = typeof(FeatureWorkflow).Assembly.GetTypes()
            .Select(type => (Type: type, Attribute: System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Temporalio.Workflows.WorkflowAttribute>(type)))
            .Where(item => item.Attribute is not null)
            .Select(item => item.Attribute!.Name ?? item.Type.Name)
            .ToList();

        Assert.NotEmpty(all);
        Assert.All(all, name => Assert.Contains(name, registered));
    }

    [Fact]
    public void Activities_and_workflows_are_registered_with_what_they_need()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GitHostActivities>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<WorkspaceActivities>());
    }
}
