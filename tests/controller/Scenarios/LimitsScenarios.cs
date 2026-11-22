namespace AiSdlcFactory.Controller.Tests.Scenarios;

/// <summary>Token budgets, daily limits and the provider's usage limit: a run stops, waits or says so, and never fails silently.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class LimitsScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text, long input = 100) => AgentReply.Says(text, input);

    private const string Gate = """{"name":"test","command":["python","-m","pytest","-q"]}""";

    private static string Config(string limits = "") =>
        $$"""{"image":"python","gates":[{{Gate}}]{{(limits.Length == 0 ? "" : $",\"limits\":{limits}")}}}""";

    /// <summary>A feature run whose developer uses <paramref name="developerInput"/> tokens in one step.</summary>
    private static FakeWorld Building(long developerInput = 100) => new FakeWorld()
        .Agent("analyst", Say(Answers.Analyst()))
        .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
        .Agent("developer", Say(Answers.Developer(), developerInput))
        .Agent("reviewer", Say(Answers.Review()))
        .Agent("acceptance", Say(Answers.Qa("met", "met")));

    private static string[] Roles(FakeWorld world) => world.Snapshot(world.AgentCalls).Select(call => call.Role).ToArray();

    // ---- the run's own budget ------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_run_that_used_its_budget_stops_before_the_next_agent_and_saves_what_it_wrote()
    {
        var world = Building(developerInput: 15_000);
        world.ProjectConfig = Config("""{"token_budget":10000}""");

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("budget_exceeded", result.Status);
        Assert.Equal(["analyst", "coverage", "developer"], Roles(world)); // the reviewer was never asked
        Assert.Empty(world.PullRequests);
        var comment = Assert.Single(world.Comments, c => c.StartsWith("**Token budget reached**"));
        Assert.Contains("15,275 tokens of its budget of 10,000", comment);
        Assert.Contains("`limits.token_budget`", comment);
        Assert.Single(world.Rescues); // what the developer wrote is saved like for any other stop
        Assert.Contains(world.Comments, c => c.StartsWith("**The factory stopped: the run used its token budget**"));
        Assert.Equal(15_275, world.Snapshot(world.UsageLogs).Single().Usage.Counted); // and the run is in the usage log with what it used
    }

    [SkippableFact]
    public async Task A_run_under_its_budget_goes_all_the_way()
    {
        var world = Building();
        world.ProjectConfig = Config("""{"token_budget":10000}""");

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.DoesNotContain(world.Comments, c => c.Contains("Token budget reached"));
    }

    [SkippableFact]
    public async Task Cached_input_that_was_only_read_again_does_not_count_against_the_budget()
    {
        // Every scripted step reads 50 cached tokens: five steps would be 250 more if they counted. A budget just above the counted 625 must not stop the run.
        var world = Building();
        world.ProjectConfig = Config("""{"token_budget":10000}""");
        world.Defaults = FactoryDefaults.Standard with { MaxTokensPerRun = 10_000 };

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(5 * 125, world.Snapshot(world.UsageLogs).Single().Usage.Counted);
    }

    [SkippableFact]
    public async Task The_operators_maximum_applies_when_the_project_sets_nothing_or_asks_for_more()
    {
        foreach (var limits in new[] { "", """{"token_budget":500000}""" })
        {
            var world = Building(developerInput: 15_000);
            world.ProjectConfig = Config(limits);
            world.Defaults = FactoryDefaults.Standard with { MaxTokensPerRun = 10_000 };

            var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

            Assert.Equal("budget_exceeded", result.Status);
            Assert.Contains("of its budget of 10,000", Assert.Single(world.Comments, c => c.StartsWith("**Token budget reached**")));
        }
    }

    [SkippableFact]
    public async Task A_project_may_set_a_lower_budget_than_the_operators_maximum()
    {
        var world = Building(developerInput: 15_000);
        world.ProjectConfig = Config("""{"token_budget":10000}""");
        world.Defaults = FactoryDefaults.Standard with { MaxTokensPerRun = 5_000_000 };

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("budget_exceeded", result.Status);
        Assert.Contains("of its budget of 10,000", Assert.Single(world.Comments, c => c.StartsWith("**Token budget reached**")));
    }

    [SkippableFact]
    public async Task A_revision_is_held_to_the_same_budget()
    {
        var world = new FakeWorld().Agent("pr-resolver", Say(Answers.Revision("done", "fixed", "fixed", "fixed"), 15_000)).Agent("reviewer", Say(Answers.Review()));
        world.ProjectConfig = Config("""{"token_budget":10000}""");

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("budget_exceeded", result.Status);
        Assert.Empty(world.Publishes);
    }

    // ---- the daily limits ----------------------------------------------------------------------------------

    private static async Task<WorkResult> StartWaitAndCloseAsync(FakeWorld world, TemporalFixture fixture, WorkRequest request, string waitingFor)
    {
        return await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartAsync(request);
            await world.WaitForCommentAsync(c => c.StartsWith("**Daily token limit reached**") && c.Contains(waitingFor));
            await handle.SignalAsync(workflow => workflow.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });
    }

    [SkippableFact]
    public async Task When_the_days_limit_is_already_used_the_run_waits_for_the_next_day_before_asking_any_agent()
    {
        var world = Building();
        world.Defaults = FactoryDefaults.Standard with { DailyTokenLimit = 50_000 };
        world.UsageToday = new UsageToday(50_000, 3_000);

        var result = await StartWaitAndCloseAsync(world, fixture, ScenarioContext.Request(), "for all repositories");

        Assert.Equal("closed", result.Status); // a person can still end a run that is waiting
        Assert.Empty(world.AgentCalls);
        var notice = Assert.Single(world.Comments, c => c.StartsWith("**Daily token limit reached**"));
        Assert.Contains("will carry on after 00:01 UTC".Replace("00:01", "00:00"), notice);
        Assert.Contains("Close the ticket to stop the run.", notice);
    }

    [SkippableFact]
    public async Task A_repository_limit_is_named_with_the_repository()
    {
        var world = Building();
        world.Defaults = FactoryDefaults.Standard with { RepoDailyTokenLimit = 20_000 };
        world.UsageToday = new UsageToday(25_000, 20_000);
        var request = ScenarioContext.Request();

        var result = await StartWaitAndCloseAsync(world, fixture, request, $"for `acme/{request.Repo}`");

        Assert.Equal("closed", result.Status);
        Assert.Empty(world.AgentCalls);
    }

    [SkippableFact]
    public async Task A_day_with_room_left_does_not_slow_the_run_and_the_totals_are_looked_up_before_every_agent()
    {
        var world = Building();
        world.Defaults = FactoryDefaults.Standard with { DailyTokenLimit = 1_000_000, RepoDailyTokenLimit = 500_000 };
        world.UsageToday = new UsageToday(10_000, 2_000);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(5, world.UsageTodayChecks);
        Assert.DoesNotContain(world.Comments, c => c.Contains("Daily token limit reached"));
    }

    [SkippableFact]
    public async Task Without_a_daily_limit_the_log_is_never_read()
    {
        var world = Building();

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal(0, world.UsageTodayChecks);
    }

    [SkippableFact]
    public async Task What_the_run_itself_has_used_today_counts_even_though_the_log_does_not_have_it_yet()
    {
        var world = Building(developerInput: 25_000);
        world.Defaults = FactoryDefaults.Standard with { DailyTokenLimit = 20_000 };
        world.UsageToday = new UsageToday(0, 0);

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartAsync(ScenarioContext.Request());
            await world.WaitForCommentAsync(c => c.StartsWith("**Daily token limit reached**"));
            await handle.SignalAsync(workflow => workflow.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("closed", result.Status);
        Assert.Equal(["analyst", "coverage", "developer"], Roles(world)); // the reviewer waited: the developer's own 25,000 are over the day's 20,000
    }

    // ---- the provider's usage limit ------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_provider_that_says_its_usage_limit_is_reached_stops_the_run_cleanly_without_asking_again()
    {
        var world = new FakeWorld().Agent("analyst", Say(Answers.Analyst())).Agent("coverage", AgentReply.UsageLimitReached);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("usage_limit", result.Status);
        Assert.Single(world.Calls("coverage")); // an ordinary failure is tried once more; this one is not, because the answer would be the same
        var comment = Assert.Single(world.Comments, c => c.StartsWith("**The model provider's usage limit is reached**"));
        Assert.Contains("You have hit your usage limit", comment);
        Assert.Contains("comment `/retry`", comment);
        Assert.Empty(world.Problems);
    }

    [SkippableFact]
    public async Task A_failure_that_is_not_a_usage_limit_is_still_an_ordinary_failure_tried_twice()
    {
        var world = new FakeWorld().Agent("analyst", AgentReply.Crashes, AgentReply.Crashes);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("analysis_failed", result.Status);
        Assert.Equal(2, world.Calls("analyst").Count());
    }
}
