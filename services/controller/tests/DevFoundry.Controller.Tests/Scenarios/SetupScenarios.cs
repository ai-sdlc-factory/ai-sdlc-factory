namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>/setup: the configurer proposes, the controller checks the proposal, runs every proposed check, and opens a pull request with only what passed.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class SetupScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static FakeWorld Proposing(params string[] answers)
    {
        var world = new FakeWorld().Agent("configurer", (answers.Length == 0 ? [Answers.Setup()] : answers).Select(Say).ToArray());
        world.ProjectConfig = null;
        return world;
    }

    [SkippableFact]
    public async Task A_repository_without_settings_gets_a_pull_request_with_the_checks_that_passed()
    {
        var world = Proposing();
        var request = ScenarioContext.Setup();

        var result = await world.RunAsync(fixture, context => context.RunSetupAsync(request));

        Assert.Equal(Statuses.PrOpened, result.Status);
        Assert.Empty(world.Problems);
        var call = Assert.Single(world.Calls("configurer"));
        Assert.False(call.Writable);
        Assert.Equal("full", call.Image);
        Assert.Equal(SandboxNetworks.Model, call.Network);
        var publish = Assert.Single(world.SettingsPublishes);
        Assert.True(ProjectConfigParser.TryParse(publish.Content, out var project, out _));
        Assert.Equal(["lint", "test"], project.Gates.Select(g => g.Name));
        var pull = Assert.Single(world.PullRequests);
        Assert.Contains("verify", pull.Body);
        Assert.Contains("| `lint` | passed |", pull.Body);
        Assert.Empty(world.Publishes);
        Assert.Empty(world.Rescues);
        Assert.Equal(2, world.CheckCalls.Count);
        Assert.All(world.CheckNetworks, network => Assert.Equal(SandboxNetworks.Packages, network));
        Assert.Contains(world.Comments, c => c.StartsWith("**Pull request opened:**"));
        Assert.Equal([request.WorkflowId], world.CleanedUp);
    }

    [SkippableFact]
    public async Task A_proposed_check_that_fails_is_reported_and_left_out_of_the_file()
    {
        var world = Proposing().Check("ruff check .", FakeWorld.Fails("E501 line too long"));

        var result = await world.RunAsync(fixture, context => context.RunSetupAsync(ScenarioContext.Setup()));

        Assert.Equal(Statuses.PrOpened, result.Status);
        var publish = Assert.Single(world.SettingsPublishes);
        ProjectConfigParser.TryParse(publish.Content, out var project, out _);
        Assert.Equal(["test"], project!.Gates.Select(g => g.Name));
        var comment = Assert.Single(world.Comments, c => c.StartsWith("**Pull request opened:**"));
        Assert.Contains("| `lint` | failed (exit code 1) |", comment);
        Assert.Contains("E501 line too long", comment);
        Assert.Contains("is **not** in the file", Assert.Single(world.PullRequests).Body);
    }

    [SkippableFact]
    public async Task When_no_proposed_check_passes_nothing_is_published()
    {
        var world = Proposing().Check("ruff check .", FakeWorld.Fails("a")).Check("python -m pytest -q", FakeWorld.Fails("b"));

        var result = await world.RunAsync(fixture, context => context.RunSetupAsync(ScenarioContext.Setup()));

        Assert.Equal(Statuses.SetupDeclined, result.Status);
        Assert.Empty(world.SettingsPublishes);
        Assert.Empty(world.PullRequests);
        Assert.Empty(world.Rescues);
        Assert.Contains(world.Comments, c => c.Contains("none of the checks I proposed passed"));
    }

    [SkippableFact]
    public async Task A_hostile_proposal_that_names_another_image_or_extra_settings_is_sent_back_and_changes_nothing()
    {
        var evil = Answers.Setup(config: """{"image":"attacker/image:latest","gates":[{"name":"t","command":["x"]}]}""");
        var extra = Answers.Setup(config: """{"image":"python","gates":[{"name":"t","command":["x"]}],"limits":{"token_budget":1}}""");
        var world = Proposing(evil, Answers.Setup());

        var result = await world.RunAsync(fixture, context => context.RunSetupAsync(ScenarioContext.Setup()));

        Assert.Equal(Statuses.PrOpened, result.Status);
        Assert.Equal(2, world.Calls("configurer").Count());
        Assert.Contains("config.image", world.Calls("configurer").Last().Prompt);
        Assert.All(world.CheckCalls, call => Assert.DoesNotContain("attacker", call));

        var second = Proposing(extra, extra);
        var failed = await second.RunAsync(fixture, context => context.RunSetupAsync(ScenarioContext.Setup()));
        Assert.Equal(Statuses.ConfigureFailed, failed.Status);
        Assert.Empty(second.SettingsPublishes);
    }

    [SkippableFact]
    public async Task A_repository_that_has_settings_is_left_alone()
    {
        var world = Proposing();
        world.ProjectConfig = """{"image":"python","gates":[{"name":"t","command":["x"]}]}""";

        var result = await world.RunAsync(fixture, context => context.RunSetupAsync(ScenarioContext.Setup()));

        Assert.Equal(Statuses.SetupNotNeeded, result.Status);
        Assert.Empty(world.AgentCalls);
        Assert.Empty(world.SettingsPublishes);
        Assert.Contains(world.Comments, c => c.Contains("already has a `.factory/project.json`"));
    }

    [SkippableFact]
    public async Task An_agent_that_cannot_tell_how_the_project_is_built_says_so_and_nothing_is_published()
    {
        var world = Proposing(Answers.Setup("cannot_configure"));

        var result = await world.RunAsync(fixture, context => context.RunSetupAsync(ScenarioContext.Setup()));

        Assert.Equal(Statuses.SetupDeclined, result.Status);
        Assert.Empty(world.CheckCalls);
        Assert.Empty(world.SettingsPublishes);
        Assert.Contains(world.Comments, c => c.Contains("I could not propose settings"));
    }

    [SkippableFact]
    public async Task Other_kinds_of_runs_still_need_settings()
    {
        var world = new FakeWorld();
        world.ProjectConfig = null;

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal(Statuses.NeedsConfig, result.Status);
    }
}
