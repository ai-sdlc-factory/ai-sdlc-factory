namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>Reviewers and QA between the automated checks and the pull request.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
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
        Assert.Contains("- test: python -m pytest -q", prompt);
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

/// <summary>The UX review: only when the project has a UI, the change touches it, and the UI change is medium or large.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class UxReviewScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static string ProjectWithUi(string ui = "{\"paths\":[\"web/**\"]}") =>
        "{\"image\":\"python\",\"gates\":[{\"name\":\"test\",\"command\":[\"python\",\"-m\",\"pytest\",\"-q\"]}],\"ui\":" + ui + "}";

    private static string Diff(params (string Path, int Lines)[] files) =>
        string.Join("\n", files.Select(f => $"diff --git a/{f.Path} b/{f.Path}\nnew file\n--- /dev/null\n+++ b/{f.Path}\n" +
            string.Join("\n", Enumerable.Range(1, f.Lines).Select(i => $"+line {i} of {f.Path}"))));

    /// <summary>A world where the developer changed these files, one round, with the diff to match.</summary>
    private static FakeWorld Changed(string? project, params (string Path, int Lines)[] files)
    {
        var report = new ChangeReport(files.Select(f => new FileChange(f.Path, FileChange.Added)).ToList(), []);
        var world = new FakeWorld { ProjectConfig = project, Diff = Diff(files) }
            .Agent("analyst", Say(Answers.Analyst()))
            .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("qa", Say(Answers.Qa("met", "met")))
            .Changes(report, report);
        return world;
    }

    [SkippableFact]
    public async Task A_project_without_a_ui_never_gets_a_ux_review_however_much_changed()
    {
        var world = Changed(
            "{\"image\":\"python\",\"gates\":[{\"name\":\"test\",\"command\":[\"pytest\"]}]}",
            ("web/a.html", 90), ("web/b.css", 90), ("web/c.js", 90));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Empty(world.Calls("ux"));
        Assert.DoesNotContain("ux", world.PullRequests[0].Body);
    }

    [SkippableFact]
    public async Task A_change_without_ui_files_skips_the_ux_review_and_the_pull_request_says_so()
    {
        var world = Changed(ProjectWithUi(), ("hello.py", 200), ("src/service.py", 200));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Empty(world.Calls("ux"));
        Assert.Contains("- **ux**: not reviewed, no UI files changed", world.PullRequests[0].Body);
    }

    [SkippableFact]
    public async Task A_small_ui_change_skips_the_ux_review_and_the_pull_request_gives_the_numbers()
    {
        var world = Changed(ProjectWithUi(), ("web/index.html", 12), ("hello.py", 300));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Empty(world.Calls("ux"));
        Assert.Contains("the UI change is small (1 UI file(s), 12 changed line(s))", world.PullRequests[0].Body);
    }

    [SkippableTheory]
    [InlineData(3, 2, "medium by files")]
    [InlineData(1, 60, "medium by lines")]
    [InlineData(9, 2, "large by files")]
    [InlineData(1, 250, "large by lines")]
    public async Task A_medium_or_large_ui_change_gets_a_ux_review_before_qa(int files, int linesPerFile, string why)
    {
        var world = Changed(ProjectWithUi(), Enumerable.Range(1, files).Select(i => ($"web/page{i}.html", linesPerFile)).ToArray())
            .Agent("ux", Say(Answers.Review(("minor", "Tighten the heading spacing."))));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.True(result.Status == "pr_opened", why);
        Assert.Single(world.Calls("ux"));
        AssertInOrder(world.Comments, "Review (reviewer): no blocking findings", "Review (ux): no blocking findings", "QA: every criterion is met");
        Assert.Contains("- **ux**: Some things to look at.", world.PullRequests[0].Body);
        Assert.Contains("Tighten the heading spacing.", world.PullRequests[0].Body);
        var order = world.AgentCalls.Select(call => call.Role).ToList();
        Assert.True(order.IndexOf("reviewer") < order.IndexOf("ux") && order.IndexOf("ux") < order.IndexOf("qa"), string.Join(",", order));
    }

    [SkippableFact]
    public async Task The_ux_agent_sees_only_the_ui_files_and_is_read_only_on_the_model_network()
    {
        var world = Changed(ProjectWithUi(), ("web/a.html", 30), ("web/b.css", 30), ("web/c.js", 30), ("src/service.py", 100))
            .Agent("ux", Say(Answers.Review()));

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        var call = Assert.Single(world.Calls("ux"));
        Assert.Contains("- added: web/a.html", call.Prompt);
        Assert.Contains("+line 1 of web/b.css", call.Prompt);
        Assert.DoesNotContain("service.py", call.Prompt);
        Assert.Contains("and 1 other changed file(s) that are not part of this review", call.Prompt);
        Assert.False(call.Writable);
        Assert.True(call.ModelAuth);
        Assert.Equal("model", call.Network);
    }

    [SkippableFact]
    public async Task The_project_can_set_what_counts_as_a_medium_ui_change()
    {
        var world = Changed(ProjectWithUi("{\"paths\":[\"web/**\"],\"medium\":{\"files\":1,\"lines\":5}}"), ("web/index.html", 8))
            .Agent("ux", Say(Answers.Review()));

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Single(world.Calls("ux"));
    }

    [SkippableFact]
    public async Task A_blocking_ux_finding_goes_back_to_the_developer_like_any_other_review()
    {
        var report = new ChangeReport([new FileChange("web/form.html", FileChange.Added)], []);
        var world = new FakeWorld { ProjectConfig = ProjectWithUi(), Diff = Diff(("web/form.html", 80)) }
            .Agent("analyst", Say(Answers.Analyst()))
            .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()), Say(Answers.Review()))
            .Agent("ux", Say(Answers.Review(("major", "The email input has no label."))), Say(Answers.Review()))
            .Agent("qa", Say(Answers.Qa("met", "met")), Say(Answers.Qa("met", "met")))
            .Changes(report, report, report, report);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(2, world.Calls("ux").Count());
        Assert.Contains("[ux, major] hello.py: The email input has no label.", world.Calls("developer").ElementAt(1).Prompt);
        Assert.DoesNotContain("[ux", world.PullRequests[0].Body.Replace("- **ux**", ""));
        Assert.Contains(world.Comments, c => c.Contains("Review (ux): changes needed"));
        // Like the other reviewers, the UX review does not hold QA back: every review step runs each round, so the
        // developer gets all the feedback at once.
        Assert.Equal(2, world.Calls("qa").Count());
    }

    [SkippableFact]
    public async Task An_unusable_ux_review_stops_the_run_before_qa_and_publishing()
    {
        var world = Changed(ProjectWithUi(), ("web/a.html", 90))
            .Agent("ux", Say("looks nice"), Say("really nice"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("review_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("ux review failed") && c.Contains("Nothing was published"));
        Assert.Empty(world.Calls("qa"));
        Assert.Empty(world.Publishes);
    }

    [SkippableFact]
    public async Task A_lost_login_during_the_ux_review_is_reported()
    {
        var world = Changed(ProjectWithUi(), ("web/a.html", 90)).Agent("ux", AgentReply.NotSignedIn);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("blocked", result.Status);
        Assert.Empty(world.Calls("qa"));
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

/// <summary>A run that cannot finish keeps its work on a branch, says so on the ticket, and never goes quiet.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class StoppedRunScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static FakeWorld Building() => new FakeWorld()
        .Agent("analyst", Say(Answers.Analyst()))
        .Agent("coverage", Say(Answers.Coverage("missing", "missing")));

    private static string StoppedComment(FakeWorld world) =>
        Assert.Single(world.Comments, comment => comment.Contains("The factory stopped"));

    [SkippableFact]
    public async Task Three_failed_rounds_save_the_work_to_a_wip_branch_and_the_ticket_is_told_where_it_is()
    {
        var failing = Enumerable.Repeat(FakeWorld.Fails("still red"), 3).ToArray();
        var world = Building()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()), Say(Answers.Developer()))
            .Check("python -m pytest -q", failing);
        var request = ScenarioContext.Request();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        Assert.Equal("build_failed", result.Status);
        Assert.Equal("factory/issue-1-wip", result.WorkBranch);
        var rescue = Assert.Single(world.Rescues);
        Assert.Equal(request.WorkflowId, rescue.Name);
        Assert.StartsWith("WIP: Add goodbye (#1), not finished", rescue.Message);
        var comment = StoppedComment(world);
        Assert.Contains("did not get through the checks and the review", comment);
        Assert.Contains("saved on the branch `factory/issue-1-wip` (2 file(s))", comment);
        Assert.Contains("http://ui/run", comment);
        Assert.Empty(world.PullRequests);
        Assert.Equal([request.WorkflowId], world.CleanedUp); // saving happens before the checkout is removed
    }

    [SkippableFact]
    public async Task The_work_is_saved_before_the_checkout_is_deleted_not_after()
    {
        var order = new List<string>();
        var failing = Enumerable.Repeat(FakeWorld.Fails("red"), 3).ToArray();
        var world = Building()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()), Say(Answers.Developer()))
            .Check("python -m pytest -q", failing);
        world.Rescue = _ =>
        {
            order.Add(world.Snapshot(world.CleanedUp).Count == 0 ? "saved first" : "saved after cleanup");
            return new RescueResult(true, "factory/issue-1-wip", 1, [], "", "http://ui/run");
        };

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal(["saved first"], order);
    }

    [SkippableTheory]
    [InlineData("developer blocked", "build_blocked", "developer reported that it could not continue")]
    [InlineData("review unusable", "review_failed", "review step could not be completed")]
    public async Task A_blocked_developer_or_an_unusable_review_also_saves_the_work(string scenario, string status, string reason)
    {
        var world = Building().Agent("developer", Say(scenario.StartsWith("developer") ? Answers.Developer("blocked") : Answers.Developer()));
        if (status == "review_failed")
        {
            world.Agent("reviewer", Say("no"), Say("no"));
        }

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal(status, result.Status);
        Assert.Equal("factory/issue-1-wip", result.WorkBranch);
        Assert.Contains(reason, StoppedComment(world));
    }

    [SkippableFact]
    public async Task A_failed_publish_saves_the_work_and_says_it_passed_everything()
    {
        var world = Building()
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("qa", Say(Answers.Qa("met", "met")));
        world.Publish = _ => throw new Temporalio.Exceptions.ApplicationFailureException("push rejected", nonRetryable: true);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("publish_failed", result.Status);
        Assert.Equal("factory/issue-1-wip", result.WorkBranch);
        Assert.Contains("passed every check and review; only publishing it as a pull request failed", StoppedComment(world));
    }

    [SkippableFact]
    public async Task When_only_opening_the_pull_request_fails_the_published_branch_is_pointed_to_and_nothing_is_saved_twice()
    {
        var world = Building()
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("qa", Say(Answers.Qa("met", "met")));
        world.PullRequestFailure = new Temporalio.Exceptions.ApplicationFailureException("forgejo down", nonRetryable: true);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("publish_failed", result.Status);
        Assert.Equal("factory/issue-1", result.WorkBranch); // the real branch, already pushed
        Assert.Empty(world.Rescues);
        Assert.Contains(world.Comments, c => c.Contains("is on the branch `factory/issue-1`") && c.Contains("Open one from that branch by hand"));
    }

    [SkippableFact]
    public async Task An_unexpected_error_no_longer_leaves_the_ticket_silent_and_still_saves_the_work()
    {
        var world = Building().Agent("developer", Say(Answers.Developer()));
        world.ChangesFailure = new Temporalio.Exceptions.ApplicationFailureException("disk full", nonRetryable: true);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("error", result.Status);
        Assert.Equal("factory/issue-1-wip", result.WorkBranch);
        Assert.Contains(world.Comments, c => c.Contains("The factory hit an unexpected error and stopped") && c.Contains("disk full"));
        Assert.Contains("an unexpected error", StoppedComment(world));
    }

    [SkippableFact]
    public async Task Nothing_to_save_is_still_explained_with_the_link_to_the_record()
    {
        var failing = Enumerable.Repeat(FakeWorld.Fails("red"), 3).ToArray();
        var world = Building()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()), Say(Answers.Developer()))
            .Check("python -m pytest -q", failing);
        world.Rescue = _ => new RescueResult(false, null, 0, ["a.py: possible secret"], "too much changed to save: 400 files changed", "http://ui/run");

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("build_failed", result.Status);
        Assert.Null(result.WorkBranch);
        var comment = StoppedComment(world);
        Assert.Contains("Nothing was saved: too much changed to save: 400 files changed.", comment);
        Assert.Contains("- a.py: possible secret", comment);
        Assert.Contains("http://ui/run", comment);
    }

    [SkippableFact]
    public async Task A_failure_to_save_never_replaces_the_real_outcome()
    {
        var failing = Enumerable.Repeat(FakeWorld.Fails("red"), 3).ToArray();
        var world = Building()
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()), Say(Answers.Developer()))
            .Check("python -m pytest -q", failing);
        world.Rescue = _ => throw new Temporalio.Exceptions.ApplicationFailureException("forgejo unreachable", nonRetryable: true);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("build_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("could not save the work so far") && c.Contains("forgejo unreachable"));
    }

    [SkippableTheory]
    [InlineData("closed")]
    [InlineData("rejected")]
    [InlineData("already_implemented")]
    public async Task Runs_that_ended_for_other_reasons_do_not_touch_the_remote(string how)
    {
        var world = how switch
        {
            "rejected" => new FakeWorld().Agent("analyst", Say(Answers.Analyst("reject"))),
            "already_implemented" => new FakeWorld().Agent("analyst", Say(Answers.Analyst())).Agent("coverage", Say(Answers.Coverage("covered", "covered"))),
            _ => new FakeWorld().Agent("analyst", Say(Answers.Analyst("needs_clarification"))),
        };

        WorkResult result;
        if (how == "closed")
        {
            result = await world.RunAsync(fixture, async context =>
            {
                var handle = await context.StartAsync(ScenarioContext.Request());
                await world.WaitForCommentAsync(c => c.Contains("I need answers before building"));
                await handle.SignalAsync(workflow => workflow.OnTicketClosedAsync());
                return await ScenarioContext.ResultAsync(handle);
            });
        }
        else
        {
            result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));
        }

        Assert.Equal(how, result.Status);
        Assert.Empty(world.Rescues);
        Assert.DoesNotContain(world.Comments, c => c.Contains("The factory stopped"));
    }

    [SkippableFact]
    public async Task A_successful_run_saves_nothing_extra()
    {
        var world = Building()
            .Agent("developer", Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("qa", Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Null(result.WorkBranch);
        Assert.Empty(world.Rescues);
    }
}
