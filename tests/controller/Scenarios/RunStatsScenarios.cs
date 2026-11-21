namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>What a finished run adds to the log besides its tokens: the rounds it used, the checks that failed and, per agent, the attempts, rejected answers and failed runs.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class RunStatsScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static FakeWorld Building() => new FakeWorld()
        .Agent("analyst", Say(Answers.Analyst()))
        .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
        .Agent("developer", Say(Answers.Developer()))
        .Agent("reviewer", Say(Answers.Review()))
        .Agent("acceptance", Say(Answers.Qa("met", "met")));

    [SkippableFact]
    public async Task A_clean_run_logs_one_round_one_attempt_per_agent_and_no_rejections()
    {
        var world = Building();

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        var stats = Assert.Single(world.Snapshot(world.UsageLogs)).Stats!;
        Assert.Equal((1, 0), (stats.Rounds, stats.GateFailures));
        Assert.Equal(["acceptance", "analyst", "coverage", "developer", "reviewer"], stats.Roles.Keys.Order());
        Assert.All(stats.Roles.Values, role => Assert.Equal((1, 0, 0), (role.Attempts, role.Rejected, role.Failed)));
        Assert.Equal(125, stats.Roles["developer"].Tokens);
    }

    [SkippableFact]
    public async Task An_unusable_answer_is_counted_as_rejected_and_the_second_attempt_as_accepted()
    {
        var world = Building().Agent("analyst", Say("this is not json"), Say(Answers.Analyst()));

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        var analyst = Assert.Single(world.Snapshot(world.UsageLogs)).Stats!.Roles["analyst"];
        Assert.Equal((2, 1, 0), (analyst.Attempts, analyst.Rejected, analyst.Failed));
        Assert.Equal(250, analyst.Tokens); // both attempts spent tokens
    }

    [SkippableFact]
    public async Task An_agent_that_fails_outright_is_counted_as_failed_not_rejected()
    {
        var world = new FakeWorld().Agent("analyst", AgentReply.Crashes, AgentReply.Crashes);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("analysis_failed", result.Status);
        var analyst = Assert.Single(world.Snapshot(world.UsageLogs)).Stats!.Roles["analyst"];
        Assert.Equal((2, 0, 2), (analyst.Attempts, analyst.Rejected, analyst.Failed));
    }

    [SkippableFact]
    public async Task A_failed_check_and_a_second_round_are_counted()
    {
        var world = Building().Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Check("python -m pytest -q", FakeWorld.Fails("1 failed"), FakeWorld.Passes());

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        var stats = Assert.Single(world.Snapshot(world.UsageLogs)).Stats!;
        Assert.Equal((2, 1), (stats.Rounds, stats.GateFailures));
        Assert.Equal(2, stats.Roles["developer"].Attempts);
    }

    [SkippableFact]
    public async Task A_run_where_no_agent_ran_logs_nothing_at_all()
    {
        var world = new FakeWorld { ProjectConfig = null };

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Empty(world.UsageLogs);
    }
}
