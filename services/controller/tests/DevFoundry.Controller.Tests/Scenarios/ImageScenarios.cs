using Temporalio.Exceptions;

namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>A repository may bring its own sandbox image (.factory/Dockerfile) when the operator allows it.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class ImageScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static FakeWorld ToBuild() => new FakeWorld()
        .Agent("analyst", Say(Answers.Analyst()))
        .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
        .Agent("developer", Say(Answers.Developer()))
        .Agent("reviewer", Say(Answers.Review()))
        .Agent("acceptance", Say(Answers.Qa("met", "met")));

    private static readonly RepoImageSource Dockerfile = new(
        "FROM dev-foundry/sandbox-dotnet:local\nRUN apt-get update && apt-get install -y redis-server",
        new Dictionary<string, string> { ["setup.sh"] = Convert.ToBase64String("#!/bin/sh\n"u8.ToArray()) });

    [SkippableFact]
    public async Task Without_a_dockerfile_nothing_changes()
    {
        var world = ToBuild();
        world.Defaults = world.Defaults with { AllowRepoImages = true };

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Empty(world.Builds);
        Assert.All(world.AgentCalls, call => Assert.Equal("python", call.Image));
        Assert.DoesNotContain(world.Comments, c => c.Contains("Dockerfile"));
    }

    [SkippableFact]
    public async Task When_allowed_the_image_is_built_and_every_run_for_the_ticket_uses_it()
    {
        var world = ToBuild();
        world.Defaults = world.Defaults with { AllowRepoImages = true };
        world.RepoImage = Dockerfile;

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        var build = Assert.Single(world.Builds);
        Assert.Contains("redis-server", build.Dockerfile);
        Assert.Equal(["setup.sh"], build.Files!.Keys);
        Assert.NotEmpty(world.AgentCalls);
        Assert.All(world.AgentCalls, call => Assert.Equal("repo-0123456789ab", call.Image)); // the analyst, developer and reviewers alike
        Assert.Contains(world.Comments, c => c.Contains("Preparing the sandbox image from `.factory/Dockerfile`"));
        Assert.Contains(world.Comments, c => c.Contains("The sandbox image is ready") && c.Contains("repo-0123456789ab"));
    }

    [SkippableFact]
    public async Task An_image_that_was_already_built_is_reused_without_a_second_announcement()
    {
        var world = ToBuild();
        world.Defaults = world.Defaults with { AllowRepoImages = true };
        world.RepoImage = Dockerfile;
        world.BuildResult = new SandboxBuildResult("repo-0123456789ab", false, 0, 500L * 1024 * 1024, "");

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Contains(world.Comments, c => c.Contains("Preparing the sandbox image"));
        Assert.DoesNotContain(world.Comments, c => c.Contains("The sandbox image is ready"));
        Assert.All(world.AgentCalls, call => Assert.Equal("repo-0123456789ab", call.Image));
    }

    [SkippableFact]
    public async Task When_the_operator_has_not_allowed_it_the_file_is_ignored_and_the_ticket_says_so()
    {
        var world = ToBuild();
        world.RepoImage = Dockerfile; // Defaults.AllowRepoImages stays false

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Empty(world.Builds);
        Assert.All(world.AgentCalls, call => Assert.Equal("python", call.Image));
        Assert.Contains(world.Comments, c => c.Contains("switched off") && c.Contains("FACTORY_ALLOW_REPO_IMAGES") && c.Contains("`python`"));
    }

    [SkippableFact]
    public async Task A_dockerfile_that_cannot_be_built_stops_the_run_instead_of_falling_back()
    {
        var world = ToBuild();
        world.Defaults = world.Defaults with { AllowRepoImages = true };
        world.RepoImage = Dockerfile;
        world.BuildFailure = new ApplicationFailureException("Repository image build: the build failed: returned a non-zero code: 100", nonRetryable: true);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("image_failed", result.Status);
        Assert.Empty(world.AgentCalls); // nothing ran in an environment the project did not intend
        var comment = Assert.Single(world.Comments, c => c.Contains("could not be used"));
        Assert.Contains("non-zero code: 100", comment);
        Assert.Contains("close and reopen this ticket", comment);
    }

    [SkippableFact]
    public async Task A_definition_that_cannot_be_read_stops_the_run_with_the_reason()
    {
        var world = ToBuild();
        world.Defaults = world.Defaults with { AllowRepoImages = true };
        world.RepoImageFailure = new ApplicationFailureException("`.factory/docker/x` is or goes through a symbolic link, which is not allowed.", nonRetryable: true);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("image_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("symbolic link"));
        Assert.Empty(world.Builds);
    }
}
