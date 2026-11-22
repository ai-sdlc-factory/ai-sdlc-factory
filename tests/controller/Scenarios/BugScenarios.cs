namespace AiSdlcFactory.Controller.Tests.Scenarios;

/// <summary>The bug workflow: reproduce with a test the factory has seen fail, diagnose, fix, prove it with the same test.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class BugScenarios(TemporalFixture fixture)
{
    private const string TestCommand = "python -m pytest -q test_hello.py";

    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static WorkRequest Bug(params string[] labels) =>
        ScenarioContext.Request(title: "repeat is broken", body: "repeat('ab', 3) returns 'abab'.", labels: ["bug", .. labels]) with { Kind = WorkIds.Bug };

    /// <summary>A bug that reproduces cleanly and is fixed in one round; tests change what they need.</summary>
    private static FakeWorld Fixable() => new FakeWorld()
        .Agent("bug-analyst", Say(Answers.BugAnalyst()))
        .Agent("reproducer", Say(Answers.Reproduction()))
        .Agent("diagnostician", Say(Answers.Diagnosis()))
        .Agent("developer", Say(Answers.Developer()))
        .Agent("reviewer", Say(Answers.Review()))
        .Changes(FakeWorld.TestOnly())
        .Check(TestCommand, FakeWorld.Fails("E assert 'abab' == 'ababab'"), FakeWorld.Passes());

    private static string[] Roles(FakeWorld world) => world.Snapshot(world.AgentCalls).Select(call => call.Role).ToArray();

    [SkippableFact]
    public async Task A_reproduced_bug_is_diagnosed_fixed_and_published_with_the_proof()
    {
        var world = Fixable();
        var request = Bug();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        Assert.Equal("pr_opened", result.Status);
        Assert.StartsWith("bug-", request.WorkflowId);
        Assert.Equal(["bug-analyst", "reproducer", "diagnostician", "developer", "reviewer"], Roles(world));
        Assert.Empty(world.Problems);
        Assert.Contains(world.Comments, c => c.Contains("fails on the unchanged code (exit code 1)") && c.Contains("assert 'abab' == 'ababab'"));
        Assert.Contains(world.Comments, c => c.Contains("**Root cause found**"));
        var pull = world.PullRequests.Single();
        Assert.Equal("Fix: repeat is broken", pull.Title);
        Assert.Contains("## Bug fix", pull.Body);
        Assert.Contains("multiplies by times - 1", pull.Body);
        Assert.Contains($"`{TestCommand}`", pull.Body);
        Assert.DoesNotContain("**qa**", pull.Body);
    }

    [SkippableFact]
    public async Task The_reproducer_works_in_a_writable_workspace_with_packages_and_the_diagnostician_does_not()
    {
        var world = Fixable();

        await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        var reproducer = world.Calls("reproducer").Single();
        Assert.True(reproducer.Writable);
        Assert.Equal(SandboxNetworks.Developer, reproducer.Network);
        var diagnostician = world.Calls("diagnostician").Single();
        Assert.False(diagnostician.Writable);
        Assert.Equal(SandboxNetworks.Model, diagnostician.Network);
    }

    [SkippableFact]
    public async Task The_developer_is_given_the_failing_test_the_observed_failure_and_the_root_cause()
    {
        var world = Fixable();

        await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        var prompt = world.Calls("developer").Single().Prompt;
        Assert.Contains("Fix this bug", prompt);
        Assert.Contains($"Command: {TestCommand}", prompt);
        Assert.Contains("assert 'abab' == 'ababab'", prompt);
        Assert.Contains("multiplies by times - 1", prompt);
    }

    [SkippableFact]
    public async Task A_test_that_passes_on_the_unchanged_code_goes_back_to_the_reproducer()
    {
        var world = new FakeWorld()
            .Agent("bug-analyst", Say(Answers.BugAnalyst()))
            .Agent("reproducer", Say(Answers.Reproduction()), Say(Answers.Reproduction()))
            .Agent("diagnostician", Say(Answers.Diagnosis()))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Changes(FakeWorld.TestOnly(), FakeWorld.TestOnly())
            // red check #1 passes (so the test shows nothing), red check #2 fails as required, then the fix passes
            .Check(TestCommand, FakeWorld.Passes(), FakeWorld.Fails("E assert 'abab' == 'ababab'"), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(2, world.Calls("reproducer").Count());
        Assert.Contains("The test passes on the unchanged code, so it does not show the bug", world.Calls("reproducer").ElementAt(1).Prompt);
        Assert.Contains(world.Comments, c => c.Contains("Reproduction attempt 1 was not accepted"));
    }

    [SkippableFact]
    public async Task Three_tests_that_never_fail_stop_the_run_before_any_code_is_written()
    {
        var world = new FakeWorld()
            .Agent("bug-analyst", Say(Answers.BugAnalyst()))
            .Agent("reproducer", Say(Answers.Reproduction()), Say(Answers.Reproduction()), Say(Answers.Reproduction()))
            .Changes(FakeWorld.TestOnly(), FakeWorld.TestOnly(), FakeWorld.TestOnly())
            .Check(TestCommand, FakeWorld.Passes(), FakeWorld.Passes(), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        Assert.Equal("cannot_reproduce", result.Status);
        Assert.Empty(world.Calls("developer"));
        Assert.Empty(world.PullRequests);
        Assert.Contains(world.Comments, c => c.Contains("could not produce a test that fails because of this bug in 3 attempts"));
    }

    [SkippableFact]
    public async Task A_command_that_cannot_run_is_not_taken_as_a_failing_test()
    {
        var world = new FakeWorld()
            .Agent("bug-analyst", Say(Answers.BugAnalyst()))
            .Agent("reproducer", Say(Answers.Reproduction()), Say(Answers.Reproduction()))
            .Agent("diagnostician", Say(Answers.Diagnosis()))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Changes(FakeWorld.TestOnly(), FakeWorld.TestOnly())
            .Check(TestCommand, FakeWorld.Fails("sh: pytest: not found", exit: 127), FakeWorld.Fails("E assert"), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("could not be run", world.Calls("reproducer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task A_reproducer_that_changes_production_code_is_sent_back()
    {
        var world = new FakeWorld()
            .Agent("bug-analyst", Say(Answers.BugAnalyst()))
            .Agent("reproducer", Say(Answers.Reproduction()), Say(Answers.Reproduction()))
            .Agent("diagnostician", Say(Answers.Diagnosis()))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Changes(FakeWorld.TestOnly("hello.py"), FakeWorld.TestOnly())
            .Check(TestCommand, FakeWorld.Fails("E assert"), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("not test files you listed", world.Calls("reproducer").ElementAt(1).Prompt);
        Assert.Contains("hello.py", world.Calls("reproducer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task A_reproducer_that_cannot_make_the_bug_appear_asks_and_continues_with_the_answer()
    {
        var world = new FakeWorld()
            .Agent("bug-analyst", Say(Answers.BugAnalyst()))
            .Agent("reproducer", Say(Answers.Reproduction("cannot_reproduce")), Say(Answers.Reproduction()))
            .Agent("diagnostician", Say(Answers.Diagnosis()))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Changes(FakeWorld.TestOnly())
            .Check(TestCommand, FakeWorld.Fails("E assert"), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartBugAsync(Bug());
            await world.WaitForCommentAsync(c => c.Contains("I could not reproduce the bug"));
            await handle.SignalAsync(workflow => workflow.OnHumanReplyAsync(new HumanReply("bob", "Use an even count like 4.")));
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("Use an even count like 4.", world.Calls("reproducer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task A_fix_that_is_larger_than_a_bug_fix_stops_with_advice_to_open_a_feature()
    {
        var world = new FakeWorld()
            .Agent("bug-analyst", Say(Answers.BugAnalyst()))
            .Agent("reproducer", Say(Answers.Reproduction()))
            .Agent("diagnostician", Say(Answers.Diagnosis("too_large")))
            .Changes(FakeWorld.TestOnly())
            .Check(TestCommand, FakeWorld.Fails("E assert"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        Assert.Equal("needs_design", result.Status);
        Assert.Empty(world.Calls("developer"));
        Assert.Contains(world.Comments, c => c.Contains("Open a feature ticket"));
    }

    [SkippableFact]
    public async Task A_developer_who_edits_the_reproduction_test_is_sent_back_before_the_checks_run()
    {
        var world = Fixable()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Changes(FakeWorld.TestOnly(), FakeWorld.OneFile(), FakeWorld.OneFile(), FakeWorld.OneFile(), FakeWorld.OneFile());
        // fingerprint #1 is taken after the red check; #2 (developer round 1) shows an edited test; #3 is restored
        world.FingerprintOf = call => new string(call == 2 ? 'b' : 'a', 64);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("You changed or removed the reproduction test (test_hello.py)", world.Calls("developer").ElementAt(1).Prompt);
        // The project's checks ran only for the second round: the first was sent back before them.
        Assert.Equal(1, world.CheckCalls.Count(call => call == "python -m pytest -q"));
    }

    [SkippableFact]
    public async Task A_fix_that_leaves_the_reproduction_test_failing_goes_back_to_the_developer()
    {
        var world = new FakeWorld()
            .Agent("bug-analyst", Say(Answers.BugAnalyst()))
            .Agent("reproducer", Say(Answers.Reproduction()))
            .Agent("diagnostician", Say(Answers.Diagnosis()))
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Changes(FakeWorld.TestOnly(), FakeWorld.OneFile(), FakeWorld.OneFile(), FakeWorld.OneFile(), FakeWorld.OneFile())
            // red fails; the first fix still fails the test (although the project's own checks pass); the second fixes it
            .Check(TestCommand, FakeWorld.Fails("E assert 'abab' == 'ababab'"), FakeWorld.Fails("E assert 'ababab' == 'abababab'"), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        Assert.Equal("pr_opened", result.Status);
        var second = world.Calls("developer").ElementAt(1).Prompt;
        Assert.Contains("still does not pass", second);
        Assert.Contains("assert 'ababab' == 'abababab'", second);
        Assert.Contains("round 2", world.PullRequests.Single().Body);
    }

    [SkippableFact]
    public async Task Quick_mode_skips_the_diagnosis_and_says_so()
    {
        var world = new FakeWorld()
            .Agent("bug-analyst", Say(Answers.BugAnalyst()))
            .Agent("reproducer", Say(Answers.Reproduction()))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Changes(FakeWorld.TestOnly())
            .Check(TestCommand, FakeWorld.Fails("E assert"), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Bug("quick")));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(["bug-analyst", "reproducer", "developer", "reviewer"], Roles(world));
        Assert.Contains(world.Comments, c => c.Contains("I skip the root-cause analysis"));
        Assert.DoesNotContain("**Root cause:**", world.PullRequests.Single().Body);
        Assert.DoesNotContain("Root cause:", world.Calls("developer").Single().Prompt);
    }

    [SkippableFact]
    public async Task A_report_the_analyst_rejects_stops_immediately()
    {
        var world = new FakeWorld().Agent("bug-analyst", Say(Answers.BugAnalyst("reject")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        Assert.Equal("rejected", result.Status);
        Assert.Equal(["bug-analyst"], Roles(world));
    }

    [SkippableFact]
    public async Task The_configured_reviewers_still_judge_the_fix_and_a_blocking_finding_sends_it_back()
    {
        var world = new FakeWorld()
            .Agent("bug-analyst", Say(Answers.BugAnalyst()))
            .Agent("reproducer", Say(Answers.Reproduction()))
            .Agent("diagnostician", Say(Answers.Diagnosis()))
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review(("major", "The loop is off by one elsewhere too."))), Say(Answers.Review()))
            .Changes(FakeWorld.TestOnly(), FakeWorld.OneFile(), FakeWorld.OneFile(), FakeWorld.OneFile(), FakeWorld.OneFile())
            .Check(TestCommand, FakeWorld.Fails("E assert"), FakeWorld.Passes(), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("[reviewer, major]", world.Calls("developer").ElementAt(1).Prompt);
        Assert.Empty(world.Calls("acceptance"));
    }

    [SkippableFact]
    public async Task The_failing_check_is_recorded_when_the_project_keeps_a_record()
    {
        var world = Fixable();
        world.ProjectConfig = """{"image":"python","record":true,"gates":[{"name":"test","command":["python","-m","pytest","-q"]}]}""";

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Bug()));

        Assert.Equal("pr_opened", result.Status);
        var red = Assert.Single(world.Records, r => r.Slug == "red-check");
        Assert.Equal("failed as required", red.Outcome);
        Assert.Equal(TestCommand, red.Input);
        Assert.Contains("assert 'abab'", red.Output);
        Assert.Contains(world.Records, r => r.Slug == "bug-analyst");
        Assert.Contains(world.Records, r => r.Slug == "reproducer");
    }
}
