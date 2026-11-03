namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>Reviewers and QA between the automated checks and the pull request.</summary>
[Collection("temporal")]
public sealed class ReviewScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    /// <summary>The analyst passes with two open criteria and the developer is ready; reviewers and QA are scripted by the test.</summary>
    private static FakeWorld ToReview(int developerRounds = 1)
    {
        var world = new FakeWorld()
            .Agent("analyst", Say(Answers.Analyst()))
            .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
            .Agent("developer", Enumerable.Range(0, developerRounds).Select(_ => Say(Answers.Developer())).ToArray());
        return world;
    }

    private static string Project(string reviewers) =>
        $$"""{"image":"python","gates":[{"name":"test","command":["python","-m","pytest","-q"]}],"reviewers":{{reviewers}}}""";

    [SkippableFact]
    public async Task Reviewers_and_QA_see_the_change_and_their_summaries_reach_the_pull_request()
    {
        var world = ToReview()
            .Agent("reviewer", Say(Answers.Review(("minor", "Consider a shorter name."), ("nit", "Trailing space."))))
            .Agent("qa", Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        var comments = world.Comments;
        Assert.Contains(comments, c => c.Contains("Review (reviewer): no blocking findings") && c.Contains("Consider a shorter name."));
        Assert.Contains(comments, c => c.Contains("QA: every criterion is met"));
        var prompt = Assert.Single(world.Calls("reviewer")).Prompt;
        Assert.Contains("<<<DIFF", prompt);
        Assert.Contains("def goodbye()", prompt);
        Assert.Contains("- modified: hello.py", prompt);
        Assert.Contains("test passed", prompt);
        var body = world.PullRequests[0].Body;
        Assert.Contains("## Review", body);
        Assert.Contains("Consider a shorter name.", body);
        Assert.Contains("- **qa**:", body);
        Assert.Empty(world.Problems);
    }

    [SkippableFact]
    public async Task A_blocking_finding_goes_back_to_the_developer_and_the_next_round_can_pass()
    {
        var world = ToReview(developerRounds: 2)
            .Agent("reviewer", Say(Answers.Review(("major", "goodbye() has no test."))), Say(Answers.Review()))
            .Agent("qa", Say(Answers.Qa("met", "met")), Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        var second = world.Calls("developer").ElementAt(1).Prompt;
        Assert.Contains("<<<FEEDBACK", second);
        Assert.Contains("[reviewer, major] hello.py: goodbye() has no test.", second);
        Assert.Equal(2, world.CheckCalls.Count); // the checks ran again after the fix
        Assert.Contains("round 2: test passed", world.PullRequests[0].Body);
    }

    [SkippableFact]
    public async Task QA_that_finds_an_unmet_criterion_sends_the_work_back()
    {
        var world = ToReview(developerRounds: 2)
            .Agent("reviewer", Say(Answers.Review()), Say(Answers.Review()))
            .Agent("qa", Say(Answers.Qa("met", "not_met")), Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("QA: 1 criterion not met") && c.Contains("no test checks this"));
        Assert.Contains("[qa] criterion not met: bye selects it. - no test checks this", world.Calls("developer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task A_criterion_QA_calls_met_without_a_real_file_does_not_count()
    {
        // QA cites test_hello.py, which does not exist in the workspace.
        var world = ToReview(developerRounds: 3);
        world.ExistingFiles = ["hello.py"];
        world.Agent("reviewer", Say(Answers.Review()), Say(Answers.Review()), Say(Answers.Review()))
            .Agent("qa", Say(Answers.Qa("met", "met")), Say(Answers.Qa("met", "met")), Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("build_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("cited files were not found"));
        Assert.Empty(world.PullRequests);
    }

    [SkippableFact]
    public async Task A_security_reviewer_runs_when_the_project_asks_for_one_and_can_block()
    {
        var world = ToReview(developerRounds: 2);
        world.ProjectConfig = Project("""["reviewer","security"]""");
        world.Agent("reviewer", Say(Answers.Review()), Say(Answers.Review()))
             .Agent("security", Say(Answers.Review(("blocker", "The bye argument is passed to a shell."))), Say(Answers.Review()))
             .Agent("qa", Say(Answers.Qa("met", "met")), Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(2, world.Calls("security").Count());
        Assert.Contains(world.Comments, c => c.Contains("Review (security): changes needed"));
        Assert.Contains("[security, blocker] hello.py: The bye argument is passed to a shell.", world.Calls("developer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task Without_a_security_reviewer_in_the_project_none_runs()
    {
        var world = ToReview()
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("qa", Say(Answers.Qa("met", "met")));

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Empty(world.Calls("security"));
    }

    [SkippableFact]
    public async Task Blocking_findings_in_every_round_publish_nothing()
    {
        var blocking = Say(Answers.Review(("blocker", "Still wrong.")));
        var world = ToReview(developerRounds: 3)
            .Agent("reviewer", blocking, blocking, blocking)
            .Agent("qa", Say(Answers.Qa("met", "met")), Say(Answers.Qa("met", "met")), Say(Answers.Qa("met", "met")));
        var request = ScenarioContext.Request();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        Assert.Equal("build_failed", result.Status);
        Assert.Equal(3, world.Calls("developer").Count());
        Assert.Empty(world.Publishes);
        Assert.Empty(world.PullRequests);
        Assert.Contains(world.Comments, c => c.Contains("through the checks and the review in 3 rounds"));
        Assert.Equal([request.WorkflowId], world.CleanedUp);
    }

    [SkippableFact]
    public async Task An_unusable_review_stops_the_run_instead_of_publishing_unreviewed_work()
    {
        var world = ToReview().Agent("reviewer", Say("looks fine to me"), Say("really, fine"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("review_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("reviewer review failed") && c.Contains("Nothing was published"));
        Assert.Empty(world.Calls("qa"));
        Assert.Empty(world.Publishes);
    }

    [SkippableFact]
    public async Task A_review_whose_verdict_contradicts_its_findings_is_refused_with_the_reason()
    {
        const string contradictory = """{"verdict":"pass","summary":"Fine.","findings":[{"severity":"major","message":"No test."}]}""";
        var world = ToReview()
            .Agent("reviewer", Say(contradictory), Say(Answers.Review()))
            .Agent("qa", Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("the verdict must be needs_changes", world.Calls("reviewer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task An_unusable_QA_answer_stops_the_run()
    {
        var world = ToReview().Agent("reviewer", Say(Answers.Review())).Agent("qa", Say("{}"), Say("{}"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("review_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("QA check failed"));
        Assert.Empty(world.Publishes);
    }

    [SkippableFact]
    public async Task A_lost_login_during_review_is_reported()
    {
        var world = ToReview().Agent("reviewer", AgentReply.NotSignedIn);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("blocked", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("OpenCode is not signed in"));
    }

    [SkippableFact]
    public async Task Reviewing_agents_are_read_only_and_get_the_model_login()
    {
        var world = ToReview().Agent("reviewer", Say(Answers.Review())).Agent("qa", Say(Answers.Qa("met", "met")));

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        foreach (var call in world.Calls("reviewer").Concat(world.Calls("qa")))
        {
            Assert.False(call.Writable);
            Assert.True(call.ModelAuth);
        }
    }
}
