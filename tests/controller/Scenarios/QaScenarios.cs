using Temporalio.Exceptions;

namespace AiSdlcFactory.Controller.Tests.Scenarios;

/// <summary>The /qa run: a tester agent writes end-to-end tests, the factory runs the suites itself, and the report goes on the ticket.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class QaScenarios(TemporalFixture fixture)
{
    private const string ApiConfig =
        """{"image":"node","gates":[{"name":"test","command":["npm","test"]}],"verify":{"tests":"e2e","suites":[{"name":"api","command":["npm","run","e2e:api"]}]}}""";

    private const string BothConfig =
        """{"image":"node","gates":[{"name":"test","command":["npm","test"]}],"verify":{"tests":"e2e","suites":[{"name":"api","command":["npm","run","e2e:api"]},{"name":"ui","command":["npm","run","e2e:ui"]}]}}""";

    private static string Say(string text) => text;

    private static AgentReply Reply(string text) => AgentReply.Says(text);

    private static ChangeReport Tests(params string[] paths) =>
        new(paths.Select(path => new FileChange(path, FileChange.Added)).ToList(), []);

    // The tests the tester wrote are seen twice: when its attempt is checked, and again before they are pushed (what the suites left behind).
    private static FakeWorld Api(string config = ApiConfig) => new FakeWorld { ProjectConfig = config }.Changes(Tests("e2e/api/goodbye.test"), Tests("e2e/api/goodbye.test"));

    private static string Report(FakeWorld world) => Assert.Single(world.Comments, c => c.Contains(QaReportFormat.Marker));

    private const string UiPathsApiOnly =
        """{"image":"node","gates":[{"name":"test","command":["npm","test"]}],"ui":{"paths":["web/**"]},"verify":{"tests":"e2e","suites":[{"name":"api","command":["npm","run","e2e:api"]}]}}""";

    private static QaContext WithChangedFiles(FakeWorld world, params string[] paths) =>
        world.QaContext with { Changes = new PullChanges(paths.Select(path => new ChangedFile(path, "modified", 3, 1)).ToList(), paths.Length, "", false) };

    [SkippableFact]
    public async Task A_ui_only_change_without_a_ui_suite_is_said_before_the_tester_runs_and_again_in_the_report()
    {
        var world = Api(UiPathsApiOnly).Agent("e2e-tester", Reply(Answers.Tester()));
        world.QaContext = WithChangedFiles(world, "web/app.tsx", "web/button.tsx");

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_reported", result.Status);
        var notice = Assert.Single(world.Comments, c => c.StartsWith("**QA: a heads up before I start**"));
        Assert.Contains("**Scope:** this pull request's change.", notice);
        Assert.Contains("All 2 files this pull request changes are UI files", notice);
        Assert.True(world.Comments.IndexOf(notice) < world.Comments.FindIndex(c => c.Contains(QaReportFormat.Marker)), "the notice comes before the report");
        Assert.Single(world.Calls("e2e-tester")); // the run still goes ahead
        Assert.Contains("**Heads up:** All 2 files this pull request changes are UI files", Report(world));
    }

    [SkippableFact]
    public async Task A_change_that_also_touches_other_files_gets_no_ui_notice()
    {
        var world = Api(UiPathsApiOnly).Agent("e2e-tester", Reply(Answers.Tester()));
        world.QaContext = WithChangedFiles(world, "web/app.tsx", "api/Handler.cs");

        await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.DoesNotContain(world.Comments, c => c.Contains("a heads up before I start"));
        Assert.DoesNotContain("Heads up", Report(world));
    }

    [SkippableFact]
    public async Task A_project_with_a_ui_suite_gets_no_ui_notice()
    {
        const string both = """{"image":"node","gates":[{"name":"test","command":["npm","test"]}],"ui":{"paths":["web/**"]},"verify":{"tests":"e2e","suites":[{"name":"api","command":["npm","run","e2e:api"]},{"name":"ui","command":["npm","run","e2e:ui"]}]}}""";
        var world = Api(both).Agent("e2e-tester", Reply(Answers.Tester()));
        world.QaContext = WithChangedFiles(world, "web/app.tsx");

        await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.DoesNotContain(world.Comments, c => c.Contains("a heads up before I start"));
    }

    [SkippableFact]
    public async Task The_blocked_comment_says_what_the_run_was_asked_to_cover()
    {
        var world = new FakeWorld { ProjectConfig = ApiConfig }.Agent("e2e-tester", Reply(Answers.Tester("blocked")));

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa() with { Mode = QaRequest.Full }));

        Assert.Equal("qa_blocked", result.Status);
        var blocked = Assert.Single(world.Comments, c => c.StartsWith("**QA: the tester could not write tests it could stand behind**"));
        Assert.Contains("**Scope:** the whole application and this pull request's change.", blocked);
    }

    [SkippableFact]
    public async Task The_tester_is_told_what_the_pull_request_changed()
    {
        var world = Api().Agent("e2e-tester", Reply(Answers.Tester()));
        world.QaContext = world.QaContext with
        {
            Changes = new PullChanges([new ChangedFile("src/greeting.py", "modified", 6, 1)], 1, "diff --git a/src/greeting.py b/src/greeting.py\n+def goodbye():\n", false),
        };

        await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        var prompt = Assert.Single(world.Calls("e2e-tester")).Prompt;
        Assert.Contains("- src/greeting.py (modified, +6 -1)", prompt);
        Assert.Contains("+def goodbye():", prompt);
        Assert.Empty(world.Problems);
    }

    [SkippableFact]
    public async Task Without_what_changed_the_tester_still_runs_on_the_title_and_the_criteria()
    {
        var world = Api().Agent("e2e-tester", Reply(Answers.Tester()));

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_reported", result.Status);
        Assert.DoesNotContain("CHANGED_FILES", Assert.Single(world.Calls("e2e-tester")).Prompt);
    }

    [SkippableFact]
    public async Task Passing_tests_are_pushed_onto_the_pull_request_and_the_report_says_what_was_used()
    {
        var world = Api().Agent("e2e-tester", Reply(Answers.Tester()));

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_reported", result.Status);
        Assert.Equal(175, result.Usage!.Total); // what the scripted step reported; kept in Temporal for every run
        Assert.Equal(100, result.Usage.Input);
        var prepare = Assert.Single(world.PrepareRequests);
        Assert.True(prepare.ExistingBranch);
        Assert.Equal("factory/issue-7", prepare.Branch);
        var call = Assert.Single(world.Calls("e2e-tester"));
        Assert.True(call.Writable);
        Assert.Equal(SandboxNetworks.Developer, call.Network);
        Assert.Contains("goodbye() returns the text.", call.Prompt);
        Assert.Contains("npm run e2e:api", call.Prompt);
        Assert.Equal(["npm run e2e:api"], world.Snapshot(world.CheckCalls).Where(c => c.StartsWith("npm run e2e")).ToList());
        var publish = Assert.Single(world.Publishes);
        Assert.True(publish.UpdateExistingBranch);
        Assert.StartsWith("Add end-to-end tests for #7", publish.Message);
        Assert.Empty(world.Rescues);
        var report = Report(world);
        Assert.Contains("no problems found", report);
        Assert.Contains("api: passed, 1 scenario", report);
        Assert.Contains("were added to the pull request in one commit", report);
        Assert.DoesNotContain("Tokens used", report);
        Assert.DoesNotContain(QaReportFormat.FindingsHeading, report);
        Assert.Empty(world.Problems);
    }

    [SkippableFact]
    public async Task The_scope_and_the_note_reach_the_tester_and_are_named_in_the_report()
    {
        var world = Api().Agent("e2e-tester", Reply(Answers.Tester()));
        var request = ScenarioContext.Qa() with { Mode = QaRequest.Full, Note = "look at the empty city id" };

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(request));

        Assert.Equal("qa_reported", result.Status);
        var prompt = Assert.Single(world.Calls("e2e-tester")).Prompt;
        Assert.Contains("Scope: the whole application and this pull request's change", prompt);
        Assert.Contains("look at the empty city id", prompt);
        var report = Report(world);
        Assert.Contains("**Scope:** the whole application and this pull request's change.", report);
        Assert.Contains("**Your note:** look at the empty city id", report);
    }

    [SkippableFact]
    public async Task A_failing_suite_goes_back_to_the_tester_and_a_real_bug_is_reported_with_the_tests_saved_not_pushed()
    {
        const string bug = """[{"test":"goodbye returns the text","verdict":"application_bug","explanation":"The endpoint returns Hello."}]""";
        var world = Api()
            .Agent("e2e-tester", Reply(Answers.Tester()), Reply(Answers.Tester(assessments: bug)))
            .Changes(Tests("e2e/api/goodbye.test"))
            .Check("npm run e2e:api", FakeWorld.Fails("expected Goodbye but got Hello"), FakeWorld.Fails("expected Goodbye but got Hello"));

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_reported", result.Status);
        Assert.Equal(2, world.Calls("e2e-tester").Count());
        Assert.Contains("expected Goodbye but got Hello", world.Calls("e2e-tester").ElementAt(1).Prompt);
        Assert.Contains("application_bug", world.Calls("e2e-tester").ElementAt(1).Prompt);
        Assert.Empty(world.Publishes); // a failing test never reaches the pull request
        Assert.Single(world.Rescues);
        var report = Report(world);
        Assert.Contains("1 finding", report);
        Assert.Contains("api: failed (exit code 1)", report);
        Assert.Contains("were **not** added to the pull request", report);
        Assert.Contains(QaReportFormat.FindingsHeading, report);
        Assert.Equal(
            ["[major] Test goodbye returns the text fails because of the application: The endpoint returns Hello."],
            QaReportFormat.ExtractFindings(report));
        Assert.Contains("`/fix qa`", report);
    }

    [SkippableFact]
    public async Task Files_the_suites_write_outside_the_tests_folder_keep_the_tests_off_the_pull_request()
    {
        var world = new FakeWorld { ProjectConfig = ApiConfig }.Agent("e2e-tester", Reply(Answers.Tester()))
            .Changes(Tests("e2e/api/goodbye.test"), new ChangeReport([new FileChange("e2e/api/goodbye.test", FileChange.Added), new FileChange("test-results/trace.zip", FileChange.Added)], []));

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_reported", result.Status);
        Assert.Empty(world.Publishes);
        Assert.Single(world.Rescues);
        var report = Report(world);
        Assert.Contains("were **not** added to the pull request", report);
        Assert.Contains("wrote files outside e2e/ (test-results/trace.zip)", report);
        Assert.Contains("api: passed", report);
    }

    [SkippableFact]
    public async Task A_wrong_test_is_fixed_by_the_tester_and_then_passes()
    {
        const string wrong = """[{"test":"goodbye returns the text","verdict":"test_problem","explanation":"Wrong selector, fixed."}]""";
        var world = Api()
            .Agent("e2e-tester", Reply(Answers.Tester()), Reply(Answers.Tester(assessments: wrong)))
            .Changes(Tests("e2e/api/goodbye.test"))
            .Check("npm run e2e:api", FakeWorld.Fails("selector not found"), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_reported", result.Status);
        Assert.Single(world.Publishes);
        Assert.Empty(world.Rescues);
        Assert.Contains("no problems found", Report(world));
    }

    [SkippableFact]
    public async Task Changes_outside_the_tests_folder_are_sent_back_and_never_run()
    {
        var world = new FakeWorld { ProjectConfig = ApiConfig }
            .Agent("e2e-tester", Reply(Answers.Tester()), Reply(Answers.Tester()))
            .Changes(new ChangeReport([new FileChange("e2e/api/goodbye.test", FileChange.Added), new FileChange("src/app.js", FileChange.Modified)], []), Tests("e2e/api/goodbye.test"), Tests("e2e/api/goodbye.test"));

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_reported", result.Status);
        Assert.Contains("You changed files outside e2e/: src/app.js", world.Calls("e2e-tester").ElementAt(1).Prompt);
        Assert.Contains(world.Comments, c => c.Contains("QA attempt 1: the tester's changes were not accepted"));
        Assert.Single(world.Snapshot(world.CheckCalls).Where(c => c.StartsWith("npm run e2e")).ToList()); // only the accepted attempt ran
        Assert.Single(world.Publishes);
    }

    [SkippableFact]
    public async Task A_large_file_the_tester_left_behind_is_removed_by_the_factory_and_does_not_cost_an_attempt()
    {
        var withLog = new ChangeReport(
            [new FileChange("e2e/api/goodbye.test", FileChange.Added), new FileChange("e2e/api-run.log", FileChange.Added)],
            ["e2e/api-run.log is larger than 1 MiB and was not scanned for secrets"])
        {
            Oversized = ["e2e/api-run.log"],
        };
        var world = new FakeWorld { ProjectConfig = ApiConfig }
            .Agent("e2e-tester", Reply(Answers.Tester()))
            .Changes(withLog, Tests("e2e/api/goodbye.test"), Tests("e2e/api/goodbye.test"));

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_reported", result.Status);
        Assert.Equal(["e2e/api-run.log"], world.RemovedOversized);
        Assert.Single(world.Calls("e2e-tester")); // no second attempt was needed
        Assert.Contains(world.Comments, c => c.Contains("left out 1 file(s)") && c.Contains("e2e/api-run.log") && c.Contains("/tmp"));
        Assert.DoesNotContain(world.Comments, c => c.Contains("were not accepted"));
        Assert.Single(world.Publishes);
    }

    [SkippableFact]
    public async Task The_same_refusal_twice_in_a_row_stops_the_run_instead_of_spending_a_third_attempt()
    {
        var outside = new ChangeReport([new FileChange("e2e/api/goodbye.test", FileChange.Added), new FileChange("src/app.js", FileChange.Modified)], []);
        var world = new FakeWorld { ProjectConfig = ApiConfig }
            .Agent("e2e-tester", Reply(Answers.Tester()), Reply(Answers.Tester()), Reply(Answers.Tester()))
            .Changes(outside, outside, outside);

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal(2, world.Calls("e2e-tester").Count());
        Assert.Contains(world.Comments, c => c.Contains("refused for the same reason twice"));
        Assert.Empty(world.Publishes);
        Assert.DoesNotContain(world.Snapshot(world.CheckCalls), c => c.StartsWith("npm run e2e"));
        Assert.NotNull(result.Status);
    }

    [SkippableFact]
    public async Task A_tester_that_cannot_write_tests_says_so_and_nothing_is_run()
    {
        var world = Api().Agent("e2e-tester", Reply(Answers.Tester("blocked")));

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_blocked", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("could not write tests") && c.Contains("does not run here"));
        Assert.Empty(world.Publishes);
        Assert.DoesNotContain(world.Snapshot(world.CheckCalls), c => c.StartsWith("npm run e2e"));
    }

    [SkippableFact]
    public async Task A_project_without_verify_settings_is_told_how_to_add_them()
    {
        var world = new FakeWorld();

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_not_configured", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("no `verify` settings"));
        Assert.Empty(world.AgentCalls);
    }

    [SkippableFact]
    public async Task Asking_for_a_suite_the_project_does_not_have_names_the_ones_it_has()
    {
        var world = Api();

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa("ui")));

        Assert.Equal("qa_not_configured", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("no `ui` suite") && c.Contains("`api`"));
        Assert.Empty(world.AgentCalls);
    }

    [SkippableFact]
    public async Task A_ticket_with_no_open_pull_request_of_the_factory_gets_an_answer()
    {
        var world = Api();
        world.QaContext = new QaContext(null, "factory-bot");

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_no_pull_request", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("could not find an open pull request"));
        Assert.Empty(world.PrepareRequests);
    }

    [SkippableFact]
    public async Task A_pull_request_that_is_not_the_factorys_is_left_alone_without_a_word()
    {
        var world = Api();
        world.QaContext = new QaContext(FakeWorld.FactoryPullRequest(author: "alice").Pull, "factory-bot");

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa(onPullRequest: true)));

        Assert.Equal("ignored", result.Status);
        Assert.Empty(world.Comments);
        Assert.Empty(world.PrepareRequests);
    }

    [SkippableFact]
    public async Task A_sandbox_that_fails_for_the_factorys_own_reasons_ends_the_run_with_a_way_to_retry()
    {
        var world = Api().Agent("e2e-tester", Reply(Answers.Tester()))
            .CheckThrows("npm run e2e:api", new ApplicationFailureException("starting the sandbox failed: docker is down", errorType: "Docker", nonRetryable: true));

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("infrastructure_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("could not run because of the factory itself") && c.Contains("/retry"));
        Assert.Empty(world.Publishes);
        Assert.DoesNotContain(world.Comments, c => c.Contains(QaReportFormat.Marker));
    }

    [SkippableFact]
    public async Task The_ui_suite_leaves_evidence_for_a_ux_review_which_is_part_of_the_report_and_is_never_pushed()
    {
        ProjectConfigParser.TryParse(BothConfig, out var project, out _);
        var verify = project!.Verify!;
        var world = new FakeWorld { ProjectConfig = BothConfig }
            .Changes(Tests("e2e/api/goodbye.test", "e2e/ui/goodbye.spec"), Tests("e2e/api/goodbye.test", "e2e/ui/goodbye.spec"), Tests("e2e/api/goodbye.test", "e2e/ui/goodbye.spec"))
            .Agent("e2e-tester", Reply(Answers.Tester(suite: "ui")))
            .Agent("ux", Reply(Answers.Review(("major", "The button has no label."))))
            .Check(string.Join(' ', VerifyScript.ReadEvidence(verify)), new SandboxRunResult(0, false, "=== e2e/evidence/home.txt\nheading: Shop\nbutton: (no label)\n", "", false));

        var result = await world.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal("qa_reported", result.Status);
        var ux = Assert.Single(world.Calls("ux"));
        Assert.False(ux.Writable);
        Assert.Equal(SandboxNetworks.Model, ux.Network);
        Assert.Contains("button: (no label)", ux.Prompt);
        Assert.Contains("<<<EVIDENCE", ux.Prompt);
        Assert.Contains(world.Snapshot(world.CheckCalls), c => c == "rm -rf -- e2e/evidence"); // removed before anything is pushed
        var report = Report(world);
        Assert.Contains("UX review: changes needed", report);
        Assert.Contains("[major] UX", report);
        Assert.Contains("The button has no label.", QaReportFormat.ExtractFindings(report).Single());
    }

    [SkippableFact]
    public async Task The_tests_folder_is_protected_for_a_feature_run_but_not_for_the_qa_run()
    {
        var feature = new FakeWorld { ProjectConfig = ApiConfig }.Agent("analyst", Reply(Answers.Analyst("reject")));
        await feature.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        var qa = Api().Agent("e2e-tester", Reply(Answers.Tester("blocked")));
        await qa.RunAsync(fixture, context => context.RunQaAsync(ScenarioContext.Qa()));

        Assert.Equal(["e2e/**"], Assert.Single(feature.ProtectedPathRequests).Patterns);
        Assert.Empty(qa.ProtectedPathRequests);
    }
}
