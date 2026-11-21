namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>What a run is likely to cost is said on the ticket, a run that wasted tokens says what it cost, and an expensive /qa run can wait for a go-ahead.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class CostGuardScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text, long input = 100) => AgentReply.Says(text, input);

    private const string QaConfig =
        """{"image":"node","gates":[{"name":"test","command":["npm","test"]}],"verify":{"tests":"e2e","suites":[{"name":"api","command":["npm","run","e2e:api"]}]}""";

    private static string QaWith(string limits) => QaConfig + $",\"limits\":{limits}}}";

    private static FakeWorld Qa(string limits, UsageEstimate estimate)
    {
        var world = new FakeWorld { ProjectConfig = QaWith(limits), Estimate = estimate }
            .Changes(new ChangeReport([new FileChange("e2e/api/goodbye.test", FileChange.Added)], []), new ChangeReport([new FileChange("e2e/api/goodbye.test", FileChange.Added)], []))
            .Agent("e2e-tester", Say(Answers.Tester()));
        return world;
    }

    private static FakeWorld Feature(UsageEstimate estimate) => new FakeWorld { Estimate = estimate }
        .Agent("analyst", Say(Answers.Analyst()))
        .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
        .Agent("developer", Say(Answers.Developer()))
        .Agent("reviewer", Say(Answers.Review()))
        .Agent("acceptance", Say(Answers.Qa("met", "met")));

    // ---- the estimate ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_run_starts_with_an_estimate_from_the_earlier_runs_of_its_kind_and_is_not_held_up_by_it()
    {
        var world = Feature(new UsageEstimate(9, 304_000));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(["feature"], world.Snapshot(world.EstimateKinds));
        var estimate = Assert.Single(world.Comments, c => c.StartsWith("**Estimate:**"));
        Assert.Contains("A feature on this repository has typically used about 300,000 tokens (the median of the last 9 runs)", estimate);
        Assert.Contains("nothing is stopped because of it", estimate);
    }

    [SkippableFact]
    public async Task Without_enough_history_the_estimate_says_so_and_names_no_figure()
    {
        var world = Feature(new UsageEstimate(2, 304_000));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        var estimate = Assert.Single(world.Comments, c => c.StartsWith("**Estimate:**"));
        Assert.Contains("no history yet for a feature", estimate);
        Assert.DoesNotContain("300,000", estimate);
    }

    [SkippableFact]
    public async Task A_question_is_cheap_and_gets_no_estimate()
    {
        var world = new FakeWorld { Estimate = new UsageEstimate(9, 300_000) }.Agent("asker", Say(Answers.Ask(files: "hello.py")));

        await world.RunAsync(fixture, context => context.RunAskAsync(ScenarioContext.Ask()));

        Assert.Empty(world.Snapshot(world.EstimateKinds));
        Assert.DoesNotContain(world.Comments, c => c.StartsWith("**Estimate:**"));
    }

    // ---- a run that wasted tokens ----------------------------------------------------------------------

    [SkippableFact]
    public async Task A_run_that_failed_says_what_it_used_and_a_successful_one_does_not()
    {
        var failing = new FakeWorld().Agent("analyst", Say("this is not json", 40_000), Say("still not json", 40_000));
        var failed = await failing.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));
        var ok = Feature(new UsageEstimate(0, 0));
        var done = await ok.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("analysis_failed", failed.Status);
        Assert.Contains(failing.Comments, c => c == "This run used about 80,000 tokens before it stopped (input, output, reasoning and cache writes).");
        Assert.Equal("pr_opened", done.Status);
        Assert.DoesNotContain(ok.Comments, c => c.Contains("before it stopped"));
    }

    [SkippableFact]
    public async Task A_run_that_failed_before_spending_anything_says_nothing_about_tokens()
    {
        var world = new FakeWorld().Agent("analyst", AgentReply.NotSignedIn);

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.DoesNotContain(world.Comments, c => c.Contains("before it stopped"));
    }

    // ---- the go-ahead for an expensive QA run ------------------------------------------------------------

    [SkippableFact]
    public async Task An_expensive_qa_run_waits_and_qa_go_starts_it()
    {
        var world = Qa("""{"confirm_over":400000}""", new UsageEstimate(5, 600_000));

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartQaAsync(ScenarioContext.Qa());
            await world.WaitForCommentAsync(c => c.StartsWith("**This QA run needs your go-ahead**"));
            Assert.Empty(world.Calls("e2e-tester")); // nothing is spent while it waits
            await handle.SignalAsync(workflow => workflow.OnGoAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("qa_reported", result.Status);
        var waiting = Assert.Single(world.Comments, c => c.StartsWith("**This QA run needs your go-ahead**"));
        Assert.Contains("about 600,000 tokens", waiting);
        Assert.Contains("400,000", waiting);
        Assert.Contains("Comment `/qa go`", waiting);
        Assert.Single(world.Calls("e2e-tester"));
        Assert.Contains(world.Comments, c => c == TicketComments.QaConfirmed);
    }

    [SkippableFact]
    public async Task Closing_the_ticket_ends_the_wait_and_nothing_is_spent()
    {
        var world = Qa("""{"confirm_over":400000}""", new UsageEstimate(5, 600_000));

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartQaAsync(ScenarioContext.Qa());
            await world.WaitForCommentAsync(c => c.StartsWith("**This QA run needs your go-ahead**"));
            await handle.SignalAsync(workflow => workflow.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("closed", result.Status);
        Assert.Empty(world.Calls("e2e-tester"));
    }

    [SkippableFact]
    public async Task A_qa_run_under_the_threshold_or_without_history_or_with_the_setting_off_does_not_wait()
    {
        foreach (var (limits, estimate) in new[]
        {
            ("""{"confirm_over":900000}""", new UsageEstimate(5, 600_000)),   // under it
            ("""{"confirm_over":400000}""", new UsageEstimate(2, 600_000)),   // no estimate to compare
            ("""{"build_rounds":3}""", new UsageEstimate(5, 600_000)),        // setting off
        })
        {
            var world = Qa(limits, estimate);

            var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

            Assert.Equal("qa_reported", result.Status);
            Assert.DoesNotContain(world.Comments, c => c.Contains("needs your go-ahead"));
        }
    }

    [SkippableFact]
    public async Task The_factorys_default_threshold_applies_when_the_project_sets_none()
    {
        var world = Qa("""{"build_rounds":3}""", new UsageEstimate(5, 600_000));
        world.Defaults = FactoryDefaults.Standard with { ConfirmOverTokens = 500_000 };

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartQaAsync(ScenarioContext.Qa());
            await world.WaitForCommentAsync(c => c.StartsWith("**This QA run needs your go-ahead**"));
            await handle.SignalAsync(workflow => workflow.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("closed", result.Status);
    }
}
