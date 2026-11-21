using System.Text.Json;
using Temporalio.Exceptions;

namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>
/// The real FeatureWorkflow on a Temporal dev server, with scripted agents, checks, workspace and git host.
/// These cover every branch a live run cannot be made to take on demand.
/// </summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class FeatureWorkflowScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    /// <summary>A world where the analyst passes with two criteria that are not built yet.</summary>
    private static FakeWorld ToBuild(bool design = false)
    {
        var world = new FakeWorld()
            .Agent("analyst", Say(Answers.Analyst(files: design ? 4 : 2, api: design)))
            .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("acceptance", Say(Answers.Qa("met", "met")));
        if (design)
        {
            world.Agent("architect", Say(Answers.Architect()));
        }
        return world;
    }

    private static void NothingUnscripted(FakeWorld world) => Assert.Empty(world.Problems);

    private static void CleanedUpOnce(FakeWorld world, WorkRequest request) => Assert.Equal([request.WorkflowId], world.CleanedUp);

    // ---- the whole way ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_finished_run_is_added_to_the_usage_log_with_its_tokens_and_how_it_ended()
    {
        var world = ToBuild().Agent("developer", Say(Answers.Developer()));
        var request = ScenarioContext.Request();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        var entry = Assert.Single(world.Snapshot(world.UsageLogs));
        Assert.Equal(request.WorkflowId, entry.WorkflowId);
        Assert.Equal("feature", entry.Kind);
        Assert.Equal($"acme/{request.Repo}", entry.Repository);
        Assert.Equal(1, entry.Number);
        Assert.Equal("pr_opened", entry.Status);
        Assert.False(string.IsNullOrEmpty(entry.RunId));
        Assert.Equal(result.Usage, entry.Usage); // the same figures as the workflow result
        Assert.Equal(5 * 175, entry.Usage.Total);
    }

    [SkippableFact]
    public async Task A_run_that_stopped_is_logged_too_because_its_tokens_were_spent()
    {
        var world = ToBuild().Agent("developer", Say(Answers.Developer("blocked")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("build_blocked", result.Status);
        Assert.Equal("build_blocked", Assert.Single(world.Snapshot(world.UsageLogs)).Status);
    }

    [SkippableFact]
    public async Task A_run_where_no_agent_ran_adds_nothing_to_the_usage_log()
    {
        var world = new FakeWorld { ProjectConfig = null }; // no settings in the repository: the run stops before any agent

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("needs_config", result.Status);
        Assert.Empty(world.UsageLogs);
    }

    [SkippableFact]
    public async Task A_misspelled_setting_is_warned_about_once_and_the_run_goes_ahead()
    {
        var world = ToBuild().Agent("developer", Say(Answers.Developer()));
        world.ProjectConfig = """{"image":"python","gates":[{"name":"test","command":["python","-m","pytest","-q"]}],"revewers":["security"]}""";

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        var notice = Assert.Single(world.Snapshot(world.Notices));
        Assert.Contains("`revewers` is not a setting the factory knows, so it is ignored. Did you mean `reviewers`?", notice);
        Assert.Contains(notice, world.Comments);
        Assert.Equal(["reviewer"], world.Calls("reviewer").Select(call => call.Role).Distinct()); // the misspelled key changed nothing
    }

    [SkippableFact]
    public async Task A_clear_small_ticket_becomes_a_pull_request()
    {
        var world = ToBuild().Agent("developer", Say(Answers.Developer()));
        var request = ScenarioContext.Request();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        Assert.Equal(new WorkResult("pr_opened", false, "http://git.test/pulls/1"), result with { Usage = null });
        // Every scripted agent step reported 175 tokens (100 in, 20 out, 5 reasoning, 50 cache read); the total of the run is kept in the result.
        Assert.Equal(5, result.Usage!.Steps);
        Assert.Equal(5 * 175, result.Usage.Total);
        NothingUnscripted(world);
        CleanedUpOnce(world, request);

        var comments = world.Comments;
        AssertInOrder(comments, "picked up this feature ticket", "Analysis: ready to build", "Coverage check: not implemented yet",
            "Triage: small change", "Build round 1: test passed", "Pull request opened");

        Assert.Equal(["python -m pytest -q"], world.CheckCalls);
        var pullRequest = Assert.Single(world.PullRequests);
        Assert.Equal(("factory/issue-1", "main", "Add goodbye"), (pullRequest.Head, pullRequest.Base, pullRequest.Title));
        Assert.StartsWith("Closes #1", pullRequest.Body);
        Assert.Contains("round 1: test passed", pullRequest.Body);
        Assert.Single(world.Publishes);
        Assert.Contains("Add goodbye (#1)", world.Publishes[0].Message);
    }

    [SkippableFact]
    public async Task Agents_get_the_rights_their_role_needs_and_no_more()
    {
        var world = ToBuild(design: true).Agent("developer", Say(Answers.Developer()));

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        var calls = world.AgentCalls.ToDictionary(call => call.Role);
        foreach (var role in new[] { "analyst", "coverage", "architect", "reviewer", "acceptance" })
        {
            Assert.False(calls[role].Writable, $"{role} must be read-only");
            Assert.True(calls[role].ModelAuth);
        }
        Assert.True(calls["developer"].Writable);
        Assert.True(calls["developer"].ModelAuth);
        Assert.All(world.AgentCalls, call => Assert.Equal("python", call.Image));
        // The project's checks never see the model login.
        Assert.Equal(["python -m pytest -q"], world.CheckCalls);
    }

    [SkippableFact]
    public async Task A_big_change_gets_a_design_that_the_developer_receives()
    {
        var world = ToBuild(design: true).Agent("developer", Say(Answers.Developer()));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.True(result.NeedsArchitect);
        AssertInOrder(world.Comments, "Triage: design review needed", "Design: ready to build", "Build round 1");
        var prompt = Assert.Single(world.Calls("developer")).Prompt;
        Assert.Contains("<<<DESIGN", prompt);
        Assert.Contains("Add goodbye() to hello.py.", prompt);
        Assert.Contains("## Design", world.PullRequests[0].Body);
    }

    [SkippableFact]
    public async Task The_label_forces_a_design_review_for_a_small_change()
    {
        var world = ToBuild(design: false).Agent("architect", Say(Answers.Architect())).Agent("developer", Say(Answers.Developer()));

        var result = await world.RunAsync(fixture, context =>
            context.RunToEndAsync(ScenarioContext.Request(labels: ["feature", "needs-architecture"])));

        Assert.True(result.NeedsArchitect);
        Assert.Contains(world.Comments, c => c.Contains("needs-architecture"));
    }

    // ---- configuration and setup --------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_repository_without_a_project_file_gets_an_example_and_nothing_runs()
    {
        var world = new FakeWorld { ProjectConfig = null };
        var request = ScenarioContext.Request();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        Assert.Equal("needs_config", result.Status);
        Assert.Empty(world.AgentCalls);
        Assert.Contains(world.Comments, c => c.Contains(".factory/project.json") && c.Contains("\"gates\"") && c.Contains("close and reopen"));
        CleanedUpOnce(world, request);
    }

    [SkippableFact]
    public async Task An_unusable_project_file_is_explained()
    {
        var world = new FakeWorld { ProjectConfig = """{"image":"python","gates":[]}""" };

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("needs_config", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("cannot be used") && c.Contains("gates"));
        Assert.Empty(world.AgentCalls);
    }

    [SkippableFact]
    public async Task A_failed_checkout_is_reported_and_still_cleaned_up()
    {
        var world = new FakeWorld { Prepare = _ => throw new ApplicationFailureException("clone failed", nonRetryable: true) };
        var request = ScenarioContext.Request();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        Assert.Equal("setup_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("Setting up the workspace failed"));
        CleanedUpOnce(world, request);
    }

    // ---- the analyst --------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task The_analyst_asks_waits_for_the_answer_and_runs_again_with_it()
    {
        var world = new FakeWorld()
            .Agent("analyst", Say(Answers.Analyst("needs_clarification")), Say(Answers.Analyst()))
            .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("acceptance", Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartAsync(ScenarioContext.Request());
            await world.WaitForCommentAsync(c => c.Contains("I need answers before building"));
            Assert.Single(world.Calls("analyst"));
            await handle.SignalAsync(workflow => workflow.OnHumanReplyAsync(new HumanReply("bob", "Use the text Goodbye, world!")));
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("pr_opened", result.Status);
        var second = world.Calls("analyst").ElementAt(1).Prompt;
        Assert.Contains("Answer 1", second);
        Assert.Contains("Use the text Goodbye, world!", second);
        NothingUnscripted(world);
    }

    [SkippableFact]
    public async Task Closing_the_ticket_while_it_waits_ends_the_run_and_cleans_up()
    {
        var world = new FakeWorld().Agent("analyst", Say(Answers.Analyst("needs_clarification")));
        var request = ScenarioContext.Request();

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartAsync(request);
            await world.WaitForCommentAsync(c => c.Contains("I need answers before building"));
            await handle.SignalAsync(workflow => workflow.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("closed", result.Status);
        Assert.Empty(world.Calls("coverage"));
        CleanedUpOnce(world, request);
    }

    [SkippableFact]
    public async Task Rejected_requests_stop_before_any_other_stage()
    {
        var world = new FakeWorld().Agent("analyst", Say(Answers.Analyst("reject")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("rejected", result.Status);
        Assert.Empty(world.Calls("coverage"));
        Assert.Empty(world.Publishes);
    }

    [SkippableFact]
    public async Task An_unusable_answer_is_retried_once_with_the_reason()
    {
        var world = ToBuild().Agent("analyst", Say("I think it is fine."), Say(Answers.Analyst()))
            .Agent("developer", Say(Answers.Developer()));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        var calls = world.Calls("analyst").ToList();
        Assert.Equal(2, calls.Count);
        Assert.DoesNotContain("could not be used", calls[0].Prompt);
        Assert.Contains("could not be used: no JSON object", calls[1].Prompt);
    }

    [SkippableFact]
    public async Task Two_unusable_answers_hand_the_ticket_to_a_person()
    {
        var world = new FakeWorld().Agent("analyst", Say("nope"), Say("still nope"));
        var request = ScenarioContext.Request();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        Assert.Equal("analysis_failed", result.Status);
        Assert.Equal(2, world.Calls("analyst").Count());
        Assert.Contains(world.Comments, c => c.Contains("needs a person to look at it"));
        CleanedUpOnce(world, request);
    }

    [SkippableFact]
    public async Task A_missing_login_is_reported_and_nothing_else_runs()
    {
        var world = new FakeWorld().Agent("analyst", AgentReply.NotSignedIn);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("blocked", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("OpenCode is not signed in"));
        Assert.Single(world.AgentCalls);
    }

    [SkippableFact]
    public async Task A_crashing_agent_is_retried_once_then_reported()
    {
        var world = new FakeWorld().Agent("analyst", AgentReply.Crashes, AgentReply.Crashes);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("analysis_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("exited with code 1"));
    }

    [SkippableFact]
    public async Task A_sandbox_that_cannot_start_is_reported_as_a_problem_of_the_factory_with_a_way_to_retry()
    {
        var world = new FakeWorld().Agent("analyst", AgentReply.DockerDown);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("infrastructure_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("docker is down") && c.Contains("/retry") && c.Contains("not with your ticket"));
    }

    // ---- coverage -----------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Everything_already_built_stops_with_nothing_to_do()
    {
        var world = new FakeWorld()
            .Agent("analyst", Say(Answers.Analyst()))
            .Agent("coverage", Say(Answers.Coverage("covered", "covered")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("already_implemented", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("already implemented"));
        Assert.Empty(world.Calls("developer"));
        Assert.Empty(world.Publishes);
    }

    [SkippableFact]
    public async Task A_covered_claim_without_a_real_file_is_downgraded_and_the_work_goes_on()
    {
        // hello.py (cited by the coverage agent) does not exist; test_hello.py (cited later by QA) does.
        var world = new FakeWorld { ExistingFiles = ["test_hello.py"] }
            .Agent("analyst", Say(Answers.Analyst()))
            .Agent("coverage", Say(Answers.Coverage("covered", "covered")))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("acceptance", Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("partly implemented") && c.Contains("cited files were not found"));
        Assert.Contains("[partial", Assert.Single(world.Calls("developer")).Prompt);
    }

    [SkippableFact]
    public async Task Only_the_criteria_still_open_reach_the_developer()
    {
        var world = new FakeWorld()
            .Agent("analyst", Say(Answers.Analyst()))
            .Agent("coverage", Say(Answers.Coverage("covered", "missing")))
            .Agent("developer", Say(Answers.Developer()));

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        var prompt = Assert.Single(world.Calls("developer")).Prompt;
        Assert.Contains("2. bye selects it. [missing]", prompt);
        Assert.DoesNotContain("1. goodbye() returns the text.", prompt);
    }

    // ---- the architect ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task The_architect_asks_a_decision_waits_and_designs_with_the_answer()
    {
        var world = new FakeWorld()
            .Agent("analyst", Say(Answers.Analyst(files: 4, api: true)))
            .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
            .Agent("architect", Say(Answers.Architect("needs_decision")), Say(Answers.Architect()))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("acceptance", Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartAsync(ScenarioContext.Request());
            await world.WaitForCommentAsync(c => c.Contains("Design: a decision is needed"));
            await handle.SignalAsync(workflow => workflow.OnHumanReplyAsync(new HumanReply("bob", "Use SQLite.")));
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("Use SQLite.", world.Calls("architect").ElementAt(1).Prompt);
        NothingUnscripted(world);
    }

    [SkippableFact]
    public async Task A_crashed_agent_run_that_is_repeated_says_so_on_the_ticket()
    {
        var world = ToBuild().Agent("developer", AgentReply.Crashes, Say(Answers.Developer()));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        var notice = Assert.Single(world.Comments, c => c.Contains("run did not finish"));
        Assert.Contains("developer", notice);
        Assert.Contains("running it once more", notice);
    }

    [SkippableFact]
    public async Task The_last_failed_attempt_does_not_claim_another_one_will_follow()
    {
        var world = new FakeWorld().Agent("analyst", AgentReply.Crashes, AgentReply.Crashes);

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Single(world.Comments, c => c.Contains("run did not finish"));
    }

    [SkippableFact]
    public async Task The_architect_can_decline_and_nothing_is_built()
    {
        var world = new FakeWorld()
            .Agent("analyst", Say(Answers.Analyst(files: 4, api: true)))
            .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
            .Agent("architect", Say(Answers.Architect("reject")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("design_rejected", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("Design: not feasible"));
        Assert.Empty(world.Calls("developer"));
    }

    [SkippableFact]
    public async Task An_unusable_design_after_the_retry_is_handed_to_a_person()
    {
        var world = new FakeWorld()
            .Agent("analyst", Say(Answers.Analyst(files: 4, api: true)))
            .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
            .Agent("architect", Say("{\"verdict\":\"pass\"}"), Say("{\"verdict\":\"pass\"}"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("design_failed", result.Status);
        Assert.Contains("summary", world.Calls("architect").ElementAt(1).Prompt);
        Assert.Empty(world.Calls("developer"));
    }

    // ---- the build loop -----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_blocked_developer_stops_the_run_with_its_reasons()
    {
        var world = ToBuild().Agent("developer", Say(Answers.Developer("blocked")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("build_blocked", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("developer is blocked") && c.Contains("A protected file must change."));
        Assert.Empty(world.Publishes);
        Assert.Empty(world.CheckCalls);
    }

    [SkippableFact]
    public async Task Rejected_changes_go_back_to_the_developer_before_any_check_runs()
    {
        var world = ToBuild()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Changes(FakeWorld.Rejected(".factory/project.json is a protected file that only a person may change"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        var calls = world.Calls("developer").ToList();
        Assert.Contains("<<<FEEDBACK", calls[1].Prompt);
        Assert.Contains("protected file", calls[1].Prompt);
        Assert.Contains(world.Comments, c => c.Contains("Build round 1: the changes were rejected"));
        Assert.Equal(["python -m pytest -q"], world.CheckCalls); // only in round 2
    }

    [SkippableFact]
    public async Task A_check_that_cannot_run_because_of_the_factory_ends_the_run_instead_of_blaming_the_developer()
    {
        var world = ToBuild()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .CheckThrows("python -m pytest -q", new ApplicationFailureException("starting the sandbox failed: docker is down", errorType: "Docker", nonRetryable: true));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("infrastructure_failed", result.Status);
        Assert.Single(world.Calls("developer")); // no second round spent on a problem the code did not cause
        Assert.Contains(world.Comments, c => c.Contains("Build round 1") && c.Contains("docker is down") && c.Contains("/retry"));
    }

    [SkippableFact]
    public async Task A_failing_check_sends_its_output_back_and_the_next_round_can_pass()
    {
        var world = ToBuild()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Check("python -m pytest -q", FakeWorld.Fails("FAILED test_hello.py::test_bye - assert 1 == 2"), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        var second = world.Calls("developer").ElementAt(1).Prompt;
        Assert.Contains("Check \"test\" failed with exit code 1", second);
        Assert.Contains("assert 1 == 2", second);
        Assert.Contains("Do not weaken or delete tests", second);
        AssertInOrder(world.Comments, "Build round 1: test failed", "Build round 2: test passed", "Pull request opened");
        Assert.Contains("round 2: test passed", world.PullRequests[0].Body);
    }

    [SkippableFact]
    public async Task A_project_can_allow_fewer_build_rounds()
    {
        var world = ToBuild()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Check("python -m pytest -q", FakeWorld.Fails("still red"), FakeWorld.Fails("still red"));
        world.ProjectConfig = world.ProjectConfig!.Replace("\"image\"", "\"limits\":{\"build_rounds\":1},\"image\"");

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("build_failed", result.Status);
        Assert.Single(world.Calls("developer"));
        Assert.Contains(world.Comments, c => c.Contains("in 1 rounds"));
    }

    [SkippableFact]
    public async Task The_factorys_default_applies_when_the_project_sets_nothing()
    {
        var world = ToBuild()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Check("python -m pytest -q", FakeWorld.Fails("still red"), FakeWorld.Fails("still red"));
        world.Defaults = new FactoryDefaults(2, 3, 3);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("build_failed", result.Status);
        Assert.Equal(2, world.Calls("developer").Count());
    }

    [SkippableFact]
    public async Task A_projects_extra_protected_paths_are_handed_to_the_workspace()
    {
        var world = ToBuild().Agent("developer", Say(Answers.Developer()));
        world.ProjectConfig = world.ProjectConfig!.Replace("\"image\"", "\"protected_paths\":[\"db/**\"],\"image\"");

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal(["db/**"], Assert.Single(world.ProtectedPathRequests).Patterns);
    }

    [SkippableFact]
    public async Task Three_failed_rounds_publish_nothing_and_say_so()
    {
        var failing = Enumerable.Repeat(FakeWorld.Fails("still red"), 3).ToArray();
        var world = ToBuild()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()), Say(Answers.Developer()))
            .Check("python -m pytest -q", failing);
        var request = ScenarioContext.Request();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        Assert.Equal("build_failed", result.Status);
        Assert.Equal(3, world.Calls("developer").Count());
        Assert.Empty(world.Publishes);
        Assert.Empty(world.PullRequests);
        Assert.Contains(world.Comments, c => c.Contains("3 rounds") && c.Contains("nothing was published"));
        CleanedUpOnce(world, request);
    }

    [SkippableFact]
    public async Task A_developer_that_changes_nothing_is_told_so()
    {
        var world = ToBuild()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Changes(new ChangeReport([], []));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("no files in the workspace changed", world.Calls("developer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task Files_created_by_the_checks_are_caught_before_publishing()
    {
        var world = ToBuild()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Changes(FakeWorld.OneFile(), FakeWorld.Rejected("312 files changed, more than the limit of 300; build output and caches belong in .gitignore"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains(".gitignore", world.Calls("developer").ElementAt(1).Prompt);
        Assert.Contains(world.Comments, c => c.Contains("Also rejected"));
    }

    [SkippableFact]
    public async Task Several_checks_all_run_and_all_failures_are_reported()
    {
        var world = ToBuild()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()));
        world.ProjectConfig = """{"image":"python","gates":[{"name":"lint","command":["ruff","check","."]},{"name":"test","command":["pytest"]}]}""";
        world.Check("ruff check .", FakeWorld.Fails("E501 line too long"), FakeWorld.Passes())
             .Check("pytest", FakeWorld.Fails("1 failed"), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(["ruff check .", "pytest", "ruff check .", "pytest"], world.CheckCalls);
        var feedback = world.Calls("developer").ElementAt(1).Prompt;
        Assert.Contains("E501 line too long", feedback);
        Assert.Contains("1 failed", feedback);
    }

    [SkippableFact]
    public async Task A_publish_with_nothing_to_commit_is_reported_not_faked()
    {
        var world = ToBuild().Agent("developer", Say(Answers.Developer()));
        world.Publish = _ => null;

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("no_changes", result.Status);
        Assert.Empty(world.PullRequests);
    }

    [SkippableFact]
    public async Task A_refused_publish_is_reported_and_no_pull_request_opens()
    {
        var world = ToBuild().Agent("developer", Say(Answers.Developer()));
        world.Publish = _ => throw new ApplicationFailureException("Refusing to publish: protected file", nonRetryable: true);
        var request = ScenarioContext.Request();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        Assert.Equal("publish_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("Publishing failed") && c.Contains("protected file"));
        Assert.Empty(world.PullRequests);
        CleanedUpOnce(world, request);
    }

    [SkippableFact]
    public async Task Text_from_the_model_cannot_ping_people_in_ticket_comments()
    {
        var world = new FakeWorld().Agent("analyst", Say(Answers.Analyst("reject").Replace("Not buildable.", "Ask @admin and @root.")));

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.All(world.Comments, c => Assert.DoesNotContain("@admin", c));
    }

    private static void AssertInOrder(IEnumerable<string> comments, params string[] fragments)
    {
        var list = comments.ToList();
        var from = 0;
        foreach (var fragment in fragments)
        {
            var index = list.FindIndex(from, c => c.Contains(fragment));
            Assert.True(index >= 0, $"expected a comment containing \"{fragment}\" after position {from}; got:\n{string.Join("\n---\n", list)}");
            from = index + 1;
        }
    }
}

/// <summary>Which network each kind of sandbox run is given.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class NetworkScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static FakeWorld Everything(bool design) => new FakeWorld()
        .Agent("analyst", Say(Answers.Analyst(files: design ? 4 : 2, api: design)))
        .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
        .Agent("architect", Say(Answers.Architect()))
        .Agent("developer", Say(Answers.Developer()))
        .Agent("reviewer", Say(Answers.Review()))
        .Agent("acceptance", Say(Answers.Qa("met", "met")));

    [SkippableFact]
    public async Task Only_the_developer_may_fetch_packages_and_every_other_agent_reaches_the_model_provider_only()
    {
        var world = Everything(design: true);

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        var networks = world.AgentCalls.ToDictionary(call => call.Role, call => call.Network);
        Assert.Equal("developer", networks["developer"]);
        foreach (var role in new[] { "analyst", "coverage", "architect", "reviewer", "acceptance" })
        {
            Assert.Equal("model", networks[role]);
        }
        Assert.Empty(world.Problems);
    }

    [SkippableFact]
    public async Task The_projects_checks_get_package_registries_and_nothing_else()
    {
        var world = Everything(design: false);

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.NotEmpty(world.CheckNetworks);
        Assert.All(world.CheckNetworks, network => Assert.Equal("packages", network));
    }

    [SkippableFact]
    public async Task The_evidence_check_needs_no_network_at_all()
    {
        var world = Everything(design: false);

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.NotEmpty(world.EvidenceNetworks);
        Assert.All(world.EvidenceNetworks, network => Assert.Equal("none", network));
    }

    [SkippableFact]
    public async Task The_security_reviewer_is_also_limited_to_the_model_provider()
    {
        var world = Everything(design: false);
        world.ProjectConfig = """{"image":"python","reviewers":["reviewer","security"],"gates":[{"name":"test","command":["pytest"]}]}""";
        world.Agent("security", Say(Answers.Review()));

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("model", Assert.Single(world.Calls("security")).Network);
    }
}
