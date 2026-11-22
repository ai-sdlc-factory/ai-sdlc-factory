namespace AiSdlcFactory.Controller.Tests.Scenarios;

/// <summary>/retry carries on from the stages an earlier run of the same ticket finished, so the same agents (and questions) are not asked twice.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class ResumeScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    /// <summary>
    /// A ticket whose first run does the analysis and the coverage check (and the design when asked) and then stops because the developer is blocked;
    /// the second run's developer succeeds. Each agent has exactly the answers the two runs together may ask for, so an extra call is a problem.
    /// </summary>
    private static FakeWorld TwoRuns(bool design = false, int analystAnswers = 1)
    {
        var world = new FakeWorld()
            .Agent("analyst", Enumerable.Repeat(Say(Answers.Analyst(files: design ? 4 : 2, api: design)), analystAnswers).ToArray())
            .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
            .Agent("developer", Say(Answers.Developer("blocked")), Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("acceptance", Say(Answers.Qa("met", "met")));
        if (design)
        {
            world.Agent("architect", Say(Answers.Architect()));
        }
        return world;
    }

    private static async Task<(WorkResult First, WorkResult Second)> RunTwiceAsync(FakeWorld world, TemporalFixture fixture, WorkRequest request, WorkRequest? second = null) =>
        await world.RunAsync(fixture, async context =>
        {
            var first = await context.RunToEndAsync(request);
            var next = await context.RunToEndAsync(second ?? request with { Resume = true });
            return (first, next);
        });

    [SkippableFact]
    public async Task A_retry_reuses_the_analysis_and_the_coverage_check_and_goes_straight_to_the_developer()
    {
        var world = TwoRuns();
        var request = ScenarioContext.Request();

        var (first, second) = await RunTwiceAsync(world, fixture, request);

        Assert.Equal("build_blocked", first.Status);
        Assert.Equal("pr_opened", second.Status);
        Assert.Empty(world.Problems); // no second call of the analyst or the coverage agent was scripted
        Assert.Single(world.Calls("analyst"));
        Assert.Single(world.Calls("coverage"));
        Assert.Equal(2, world.Calls("developer").Count());
        Assert.Contains("goodbye() returns the text.", world.Calls("developer").ElementAt(1).Prompt); // the criteria came from the saved analysis
        Assert.Equal([request.WorkflowId], world.Snapshot(world.ResumeLoads));
        Assert.Contains(world.Comments, c => c.StartsWith("Carrying on from the earlier run: I keep the analysis and the coverage check"));
        Assert.Contains("`/retry restart`", world.Comments.Single(c => c.StartsWith("Carrying on")));
        Assert.Empty(world.Resumes); // a pull request is open, so nothing is left to resume
    }

    [SkippableFact]
    public async Task A_run_that_stops_keeps_what_it_finished_for_the_next_retry()
    {
        var world = TwoRuns();
        var request = ScenarioContext.Request();

        await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        var saved = Assert.Single(world.Resumes);
        Assert.Equal(request.WorkflowId, saved.Key);
        Assert.NotNull(saved.Value.Analysis);
        Assert.NotNull(saved.Value.Coverage);
        Assert.Null(saved.Value.Design); // triage did not ask for one
        Assert.Empty(world.ResumeLoads); // a first run reads nothing
    }

    [SkippableFact]
    public async Task A_retry_reuses_the_design_too_when_there_was_one()
    {
        var world = TwoRuns(design: true);
        var request = ScenarioContext.Request();

        var (_, second) = await RunTwiceAsync(world, fixture, request);

        Assert.Equal("pr_opened", second.Status);
        Assert.Empty(world.Problems);
        Assert.Single(world.Calls("architect"));
        Assert.Contains("I keep the analysis and the coverage check and the design", world.Comments.Single(c => c.StartsWith("Carrying on")));
        Assert.Contains("Add goodbye() to hello.py.", world.Calls("developer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task Answers_people_gave_to_the_analysts_questions_are_not_asked_for_again()
    {
        // First run: the analyst asks a question, a person answers, the analysis passes, and then the developer is blocked.
        var world = new FakeWorld()
            .Agent("analyst", Say(Answers.Analyst("needs_clarification")), Say(Answers.Analyst()))
            .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
            .Agent("developer", Say(Answers.Developer("blocked")), Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("acceptance", Say(Answers.Qa("met", "met")));
        var request = ScenarioContext.Request();

        var (first, second) = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartAsync(request);
            await world.WaitForCommentAsync(c => c.Contains("Which text?"));
            await handle.SignalAsync(workflow => workflow.OnHumanReplyAsync(new HumanReply("bob", "The text is Goodbye, world!")));
            var firstResult = await ScenarioContext.ResultAsync(handle);
            return (firstResult, await context.RunToEndAsync(request with { Resume = true }));
        });

        Assert.Equal("build_blocked", first.Status);
        Assert.Equal("pr_opened", second.Status);
        Assert.Equal(2, world.Calls("analyst").Count()); // both in the first run
        Assert.Empty(world.Problems);
    }

    [SkippableFact]
    public async Task A_ticket_that_was_edited_since_is_analysed_again_and_the_comment_says_why()
    {
        var world = TwoRuns(analystAnswers: 2);
        world.Agent("coverage", Say(Answers.Coverage("missing", "missing")), Say(Answers.Coverage("missing", "missing")));
        var request = ScenarioContext.Request();

        var (_, second) = await RunTwiceAsync(world, fixture, request, second: request with { Resume = true, Body = "Add a goodbye greeting, and a farewell too." });

        Assert.Equal("pr_opened", second.Status);
        Assert.Equal(2, world.Calls("analyst").Count());
        Assert.Contains(world.Comments, c => c == TicketComments.ResumeNotUsable("the ticket's text or labels changed since the earlier run"));
        Assert.DoesNotContain(world.Comments, c => c.StartsWith("Carrying on"));
    }

    [SkippableFact]
    public async Task New_commits_that_only_touch_documentation_do_not_make_the_saved_results_stale()
    {
        var world = TwoRuns(analystAnswers: 1);
        world.Agent("coverage", Say(Answers.Coverage("missing", "missing")));
        var runs = 0;
        world.Prepare = prepare => new WorkspaceInfo(prepare.Name, $"/work/{prepare.Name}", prepare.Branch, "main", ++runs == 1 ? "aaaa111" : "bbbb222");
        world.ChangedSinceFiles = ["README.md", "docs/setup.md"];
        var request = ScenarioContext.Request();

        var (_, second) = await RunTwiceAsync(world, fixture, request);

        Assert.Equal("pr_opened", second.Status);
        Assert.Single(world.Calls("analyst"));
        Assert.Single(world.Calls("coverage"));
        Assert.Contains(world.Comments, c => c.Contains("Carrying on from the earlier run"));
        Assert.DoesNotContain(world.Comments, c => c.Contains("default branch has new commits"));
    }

    [SkippableFact]
    public async Task New_commits_that_touch_code_still_make_the_saved_results_stale()
    {
        var world = TwoRuns(analystAnswers: 2);
        world.Agent("coverage", Say(Answers.Coverage("missing", "missing")), Say(Answers.Coverage("missing", "missing")));
        var runs = 0;
        world.Prepare = prepare => new WorkspaceInfo(prepare.Name, $"/work/{prepare.Name}", prepare.Branch, "main", ++runs == 1 ? "aaaa111" : "bbbb222");
        world.ChangedSinceFiles = ["README.md", "src/app.py"];

        await RunTwiceAsync(world, fixture, ScenarioContext.Request());

        Assert.Equal(2, world.Calls("analyst").Count());
        Assert.Contains(world.Comments, c => c.Contains("default branch has new commits"));
    }

    [SkippableFact]
    public async Task New_commits_on_the_default_branch_make_the_saved_results_stale()
    {
        var world = TwoRuns(analystAnswers: 2);
        world.Agent("coverage", Say(Answers.Coverage("missing", "missing")), Say(Answers.Coverage("missing", "missing")));
        var runs = 0;
        world.Prepare = prepare => new WorkspaceInfo(prepare.Name, $"/work/{prepare.Name}", prepare.Branch, "main", ++runs == 1 ? "aaaa111" : "bbbb222");
        var request = ScenarioContext.Request();

        var (_, second) = await RunTwiceAsync(world, fixture, request);

        Assert.Equal("pr_opened", second.Status);
        Assert.Equal(2, world.Calls("analyst").Count());
        Assert.Equal(2, world.Calls("coverage").Count());
        Assert.Contains(world.Comments, c => c.Contains("default branch has new commits"));
    }

    [SkippableFact]
    public async Task Without_the_retry_flag_a_new_run_starts_from_the_beginning_whatever_is_saved()
    {
        var world = TwoRuns(analystAnswers: 2);
        world.Agent("coverage", Say(Answers.Coverage("missing", "missing")), Say(Answers.Coverage("missing", "missing")));
        var request = ScenarioContext.Request();

        // Reopening a ticket, or `/retry restart`, starts a run with Resume off.
        var (_, second) = await RunTwiceAsync(world, fixture, request, second: request);

        Assert.Equal("pr_opened", second.Status);
        Assert.Equal(2, world.Calls("analyst").Count());
        Assert.Empty(world.ResumeLoads);
        Assert.DoesNotContain(world.Comments, c => c.StartsWith("Carrying on") || c == TicketComments.ResumeNothingSaved);
    }

    [SkippableFact]
    public async Task A_retry_with_nothing_saved_says_so_and_starts_from_the_beginning()
    {
        var world = new FakeWorld()
            .Agent("analyst", Say(Answers.Analyst()))
            .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("acceptance", Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request() with { Resume = true }));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains(TicketComments.ResumeNothingSaved, world.Comments);
        Assert.Single(world.Calls("analyst"));
    }

    [SkippableFact]
    public async Task A_bug_retry_reuses_the_bug_analysis_but_reproduces_again()
    {
        const string testCommand = "python -m pytest -q test_hello.py";
        var world = new FakeWorld()
            .Agent("bug-analyst", Say(Answers.BugAnalyst()))
            .Agent("reproducer", Say(Answers.Reproduction()), Say(Answers.Reproduction()), Say(Answers.Reproduction()), Say(Answers.Reproduction()))
            .Agent("diagnostician", Say(Answers.Diagnosis()))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Changes(FakeWorld.TestOnly(), FakeWorld.TestOnly(), FakeWorld.TestOnly(), FakeWorld.TestOnly())
            // The first run's three attempts never fail the test; the second run's reproduction does, and the fix then passes.
            .Check(testCommand, FakeWorld.Passes(), FakeWorld.Passes(), FakeWorld.Passes(), FakeWorld.Fails("E assert"), FakeWorld.Passes());
        var request = ScenarioContext.Request(title: "repeat is broken", body: "repeat('ab', 3) returns 'abab'.", labels: ["bug"]) with { Kind = WorkIds.Bug };

        var (first, second) = await RunTwiceAsync(world, fixture, request);

        Assert.Equal("cannot_reproduce", first.Status);
        Assert.Equal("pr_opened", second.Status);
        Assert.Single(world.Calls("bug-analyst")); // not asked again
        Assert.Equal(4, world.Calls("reproducer").Count()); // the test files live in the checkout, which is gone, so this is done again
        Assert.Contains("I keep the analysis", world.Comments.Single(c => c.StartsWith("Carrying on")));
        Assert.Empty(world.Problems);
    }
}
