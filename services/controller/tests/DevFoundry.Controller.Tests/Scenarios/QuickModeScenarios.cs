namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>Quick mode: chosen by a person, skips coverage, design review and QA, keeps everything that guards the code.</summary>
[Collection("temporal")]
public sealed class QuickModeScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static string Project(string extra = "") =>
        """{"image":"python","gates":[{"name":"test","command":["python","-m","pytest","-q"]}]""" + extra + "}";

    private static FakeWorld Quick(string? project = null) => new FakeWorld { ProjectConfig = project ?? Project() }
        .Agent("analyst", Say(Answers.Analyst()))
        .Agent("developer", Say(Answers.Developer()))
        .Agent("reviewer", Say(Answers.Review()));

    private static FeatureRequest Labelled(params string[] labels) => ScenarioContext.Request(labels: ["feature", .. labels]);

    private static string[] Roles(FakeWorld world) => world.Snapshot(world.AgentCalls).Select(call => call.Role).ToArray();

    [SkippableFact]
    public async Task The_quick_label_builds_with_only_the_analyst_the_developer_and_the_reviewer()
    {
        var world = Quick();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Labelled("quick")));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(["analyst", "developer", "reviewer"], Roles(world));
        Assert.Empty(world.Problems); // nothing unscripted was called: no coverage, architect or QA
        Assert.Single(world.CheckCalls); // the project's own checks still ran
        Assert.Contains(world.Comments, c => c.Contains("**Quick mode**") && c.Contains("the ticket has the \"quick\" label"));
        Assert.DoesNotContain(world.Comments, c => c.Contains("**Triage"));
        Assert.Contains(world.Comments, c => c.EndsWith("Asking the reviewers."));
        Assert.DoesNotContain(world.Comments, c => c.Contains("Asking the reviewers and QA"));
        var body = world.PullRequests[0].Body;
        Assert.Contains("## Quick mode", body);
        Assert.DoesNotContain("**qa**", body);
    }

    [SkippableFact]
    public async Task Every_criterion_goes_to_the_developer_because_nothing_was_checked_as_already_covered()
    {
        var world = Quick();

        await world.RunAsync(fixture, context => context.RunToEndAsync(Labelled("quick")));

        var prompt = world.Calls("developer").Single().Prompt;
        Assert.Contains("goodbye() returns the text.", prompt);
        Assert.Contains("bye selects it.", prompt);
    }

    [SkippableFact]
    public async Task A_design_review_triage_would_have_asked_for_is_skipped_and_the_ticket_says_so()
    {
        var world = new FakeWorld { ProjectConfig = Project() }
            .Agent("analyst", Say(Answers.Analyst(api: true, dependency: true)))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Labelled("quick")));

        Assert.Equal("pr_opened", result.Status);
        Assert.DoesNotContain("architect", Roles(world));
        Assert.Contains(world.Comments, c => c.Contains("Triage would have asked for a design review (a public API changes; a new dependency is needed)"));
    }

    [SkippableFact]
    public async Task A_person_asking_for_a_design_review_still_gets_one_in_quick_mode()
    {
        var world = Quick().Agent("architect", Say(Answers.Architect()));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Labelled("quick", "needs-architecture")));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(["analyst", "architect", "developer", "reviewer"], Roles(world));
        Assert.DoesNotContain("coverage", Roles(world));
        Assert.DoesNotContain("qa", Roles(world));
    }

    [SkippableFact]
    public async Task The_configured_reviewers_all_still_run()
    {
        var world = Quick(Project(",\"reviewers\":[\"reviewer\",\"security\"]")).Agent("security", Say(Answers.Review()));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Labelled("quick")));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(["analyst", "developer", "reviewer", "security"], Roles(world));
    }

    [SkippableFact]
    public async Task A_blocking_finding_still_sends_the_work_back_to_the_developer()
    {
        var world = new FakeWorld { ProjectConfig = Project() }
            .Agent("analyst", Say(Answers.Analyst()))
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review(("major", "goodbye() has no test."))), Say(Answers.Review()))
            .Changes(FakeWorld.OneFile(), FakeWorld.OneFile(), FakeWorld.OneFile(), FakeWorld.OneFile());

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Labelled("quick")));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("[reviewer, major] hello.py: goodbye() has no test.", world.Calls("developer").ElementAt(1).Prompt);
        Assert.Equal(2, world.CheckCalls.Count);
    }

    [SkippableFact]
    public async Task Failing_checks_still_stop_a_quick_run_after_three_rounds()
    {
        var failing = Enumerable.Repeat(FakeWorld.Fails("still red"), 3).ToArray();
        var world = Quick().Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()), Say(Answers.Developer()))
            .Check("python -m pytest -q", failing);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Labelled("quick")));

        Assert.Equal("build_failed", result.Status);
        Assert.Empty(world.PullRequests);
    }

    [SkippableFact]
    public async Task A_project_can_default_to_quick_and_the_full_label_overrides_it()
    {
        var quickByDefault = Project(",\"mode\":\"quick\"");

        var quick = Quick(quickByDefault);
        var quickResult = await quick.RunAsync(fixture, context => context.RunToEndAsync(Labelled()));
        Assert.Equal("pr_opened", quickResult.Status);
        Assert.Equal(["analyst", "developer", "reviewer"], Roles(quick));
        Assert.Contains(quick.Comments, c => c.Contains("the project's default mode is quick"));

        var full = Quick(quickByDefault).Agent("coverage", Say(Answers.Coverage("missing", "missing"))).Agent("qa", Say(Answers.Qa("met", "met")));
        var fullResult = await full.RunAsync(fixture, context => context.RunToEndAsync(Labelled("full")));
        Assert.Equal("pr_opened", fullResult.Status);
        Assert.Equal(["analyst", "coverage", "developer", "reviewer", "qa"], Roles(full));
        Assert.DoesNotContain(full.Comments, c => c.Contains("**Quick mode**"));
    }

    [SkippableFact]
    public async Task Without_the_label_or_a_project_default_the_full_process_runs()
    {
        var world = Quick().Agent("coverage", Say(Answers.Coverage("missing", "missing"))).Agent("qa", Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Labelled()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(["analyst", "coverage", "developer", "reviewer", "qa"], Roles(world));
        Assert.DoesNotContain("## Quick mode", world.PullRequests[0].Body);
    }
}
