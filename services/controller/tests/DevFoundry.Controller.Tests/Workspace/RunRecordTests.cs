using System.Diagnostics;
using DevFoundry.Controller.Tests.Scenarios;

namespace DevFoundry.Controller.Tests;

public sealed class RunRecordFormatTests
{
    private static RecordEntry Entry(string input = "the prompt", string output = "the answer", int sequence = 1, string kind = "agent") =>
        new("feature-acme-shop-1-abcd1234", sequence, kind, kind == "agent" ? "analyst" : "gate-test", kind == "agent" ? "analyst" : "check: test",
            1, "2026-11-03 10:00:00Z", 12.34, "accepted", 0, false, input, output);

    [Fact]
    public void A_step_shows_what_the_agent_was_told_and_what_it_answered()
    {
        var text = RunRecord.Render(Entry("Add a goodbye function", "{\"verdict\":\"pass\"}"), []);

        Assert.Contains("# analyst", text);
        Assert.Contains("- outcome: accepted", text);
        Assert.Contains("- duration: 12.3 s", text);
        Assert.Contains("## Input (the prompt)", text);
        Assert.Contains("Add a goodbye function", text);
        Assert.Contains("## Output (the answer)", text);
        Assert.Contains("{\"verdict\":\"pass\"}", text);
    }

    [Fact]
    public void Lines_that_look_like_credentials_are_never_written()
    {
        var text = RunRecord.Render(
            Entry("password = \"hunter2hunter2hunter2\"\nsafe line", "token is factory-token-0123456789 here\nAKIAABCDEFGHIJKLMNOP"),
            ["factory-token-0123456789"]);

        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("factory-token-0123456789", text);
        Assert.DoesNotContain("AKIAABCDEFGHIJKLMNOP", text);
        Assert.Contains("safe line", text);
        Assert.Contains("[line left out: possible secret]", text);
    }

    [Fact]
    public void Backticks_in_the_content_cannot_close_the_code_block_early()
    {
        var text = RunRecord.Render(Entry("```\n# not a heading\n```` more"), []);

        var lines = text.Split('\n');
        var opening = lines.First(l => l.StartsWith("`") && l.EndsWith("text"));
        var fence = opening[..^4];
        Assert.True(fence.Length >= 5, fence); // longer than the four backticks inside the content
        // The content never produces a line equal to the fence, so only the real closing line matches.
        Assert.Equal(1, lines.Count(l => l == fence));
    }

    [Fact]
    public void Very_long_content_is_cut_and_says_so()
    {
        var text = RunRecord.Render(Entry(input: new string('a', RunRecord.MaxInputChars + 500), output: new string('b', RunRecord.MaxOutputChars + 700), kind: "gate"), []);

        Assert.Contains("500 more characters left out", text);
        Assert.Contains("the first 700 characters were left out", text); // for a check the end matters
        Assert.True(text.Length < RunRecord.MaxInputChars + RunRecord.MaxOutputChars + 2000);
    }

    [Fact]
    public void The_summary_links_every_step_and_the_index_lists_them_in_order()
    {
        var entries = new[] { Entry(sequence: 2), Entry(sequence: 1, kind: "gate") };
        var spec = new RunRecordSpec(".factory/runs/issue-1-abcd1234", "acme/shop#1: Add goodbye", "Published as a pull request.");

        var summary = RunRecord.Summary(spec, entries);
        var index = RunRecord.Index(entries);

        Assert.Contains("# acme/shop#1: Add goodbye", summary);
        Assert.Contains("Published as a pull request.", summary);
        Assert.Contains("[check: test](001-gate-test.md)", summary);
        Assert.Contains("[analyst](002-analyst.md)", summary);
        Assert.True(index.IndexOf("001-gate-test.md") < index.IndexOf("002-analyst.md"));
    }

    [Theory]
    [InlineData(1, "0123456789ab-cdef", ".factory/runs/issue-1-01234567")]
    [InlineData(42, "ABCDEF12-0000", ".factory/runs/issue-42-abcdef12")]
    public void The_directory_is_the_ticket_plus_the_start_of_the_run_id(int issue, string runId, string expected) =>
        Assert.Equal(expected, RunRecord.DirectoryFor(issue, runId));

    [Theory]
    [InlineData(".factory/runs/issue-1-abcd1234", true)]
    [InlineData(".factory/runs/../../etc", false)]
    [InlineData(".factory/runs/a/b", false)]
    [InlineData(".factory/other/issue-1", false)]
    [InlineData("/.factory/runs/issue-1", false)]
    [InlineData(".factory/runs/", false)]
    public void Only_a_single_directory_under_the_runs_folder_is_allowed(string directory, bool allowed) =>
        Assert.Equal(allowed, RunRecord.IsValidDirectory(directory));
}

public sealed class RecordSettingTests
{
    private static string Config(string extra) =>
        """{"image":"python","gates":[{"name":"test","command":["pytest"]}]""" + extra + "}";

    [Fact]
    public void Recording_is_off_unless_the_project_turns_it_on()
    {
        Assert.True(ProjectConfigParser.TryParse(Config(""), out var config, out _));
        Assert.False(config.Record);
    }

    [Theory]
    [InlineData(",\"record\":true", true)]
    [InlineData(",\"record\":false", false)]
    public void The_project_can_turn_recording_on_or_off(string extra, bool expected)
    {
        Assert.True(ProjectConfigParser.TryParse(Config(extra), out var config, out var error), error);
        Assert.Equal(expected, config.Record);
    }

    [Theory]
    [InlineData(",\"record\":\"yes\"")]
    [InlineData(",\"record\":1")]
    [InlineData(",\"record\":{}")]
    [InlineData(",\"record\":null")]
    public void Anything_but_true_or_false_is_refused_with_a_message(string extra)
    {
        Assert.False(ProjectConfigParser.TryParse(Config(extra), out _, out var error));
        Assert.Contains("\"record\" must be true or false", error);
    }
}

public sealed class RecordPublishTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly string origin;
    private const string Name = "feature-acme-shop-1-abcd1234";
    private const string Directory = ".factory/runs/issue-1-abcd1234";

    public RecordPublishTests()
    {
        root = Path.Combine(temp, "root");
        System.IO.Directory.CreateDirectory(root);
        origin = Seed();
    }

    public void Dispose() => System.IO.Directory.Delete(temp, recursive: true);

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "t";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "t@example.com";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private string Seed()
    {
        var seed = Path.Combine(temp, "seed");
        var bare = Path.Combine(temp, "origin.git");
        System.IO.Directory.CreateDirectory(seed);
        Git(seed, "init", "--quiet", "--initial-branch=main");
        File.WriteAllText(Path.Combine(seed, "app.py"), "print('hello')\n");
        System.IO.Directory.CreateDirectory(Path.Combine(seed, ".factory"));
        File.WriteAllText(Path.Combine(seed, ".factory", "project.json"), "{}");
        Git(seed, "add", ".");
        Git(seed, "commit", "--quiet", "-m", "seed");
        System.IO.Directory.CreateDirectory(bare);
        Git(bare, "init", "--quiet", "--bare", "--initial-branch=main");
        Git(seed, "push", "--quiet", bare, "main");
        return bare;
    }

    private GitWorkspace Workspace(string token = "", string agents = "") =>
        new(new WorkerSettings { WorkspaceRoot = root, ForgejoToken = token, AgentsDirectory = agents }, (_, _) => origin);

    private static async Task<WorkspaceInfo> PrepareWithChange(GitWorkspace workspace)
    {
        var info = await workspace.PrepareAsync(new WorkspaceRequest("acme", "shop", Name, "factory/issue-1"), CancellationToken.None);
        File.AppendAllText($"{info.Path}/app.py", "print('more')\n");
        return info;
    }

    private static RecordEntry Step(int sequence, string slug, string input, string output) =>
        new(Name, sequence, "agent", slug, slug, 1, "2026-11-03 10:00:00Z", 1.5, "accepted", 0, false, input, output);

    private string[] Tree(string branch) => Git(origin, "ls-tree", "-r", "--name-only", branch).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static RunRecordSpec Spec() => new(Directory, "acme/shop#1: Add things", "Published as a pull request.");

    [Fact]
    public async Task A_published_branch_carries_the_prompts_and_answers_of_the_run()
    {
        var workspace = Workspace();
        await PrepareWithChange(workspace);
        await workspace.RecordAsync(Step(1, "analyst", "Add things to the app", "{\"verdict\":\"pass\"}"));
        await workspace.RecordAsync(Step(2, "developer", "Implement it", "{\"verdict\":\"done\"}"));

        var result = await workspace.PublishAsync(new PublishRequest(Name, "Add things", Spec()), CancellationToken.None);

        var tree = Tree(result!.Branch);
        Assert.Contains($"{Directory}/001-analyst.md", tree);
        Assert.Contains($"{Directory}/002-developer.md", tree);
        Assert.Contains($"{Directory}/README.md", tree);
        Assert.Contains($"{Directory}/index.json", tree);
        Assert.Contains("Add things to the app", Git(origin, "show", $"{result.Branch}:{Directory}/001-analyst.md"));
        Assert.Contains("[developer](002-developer.md)", Git(origin, "show", $"{result.Branch}:{Directory}/README.md"));
    }

    [Fact]
    public async Task The_branch_gets_a_usage_file_and_the_configured_model_of_each_role()
    {
        var agents = Path.Combine(temp, "agents");
        System.IO.Directory.CreateDirectory($"{agents}/agents");
        File.WriteAllText($"{agents}/agents/developer.md", "---\nmodel: openai/test-model\n---\nPrompt\n");
        var workspace = Workspace(agents: agents);
        await PrepareWithChange(workspace);
        await workspace.RecordAsync(Step(1, "developer", "Implement it", "done") with { Usage = new TokenUsage(100, 20, 5, 50, 0, 175, 0.01, 1) });
        await workspace.RecordAsync(Step(2, "developer", "Implement it again", "done") with { Usage = new TokenUsage(10, 2, 0, 5, 0, 17, 0, 1) });

        var result = await workspace.PublishAsync(new PublishRequest(Name, "Add things", Spec()), CancellationToken.None);

        Assert.Contains($"{Directory}/usage.json", Tree(result!.Branch));
        var usage = System.Text.Json.JsonDocument.Parse(Git(origin, "show", $"{result.Branch}:{Directory}/usage.json")).RootElement;
        Assert.Equal(192, usage.GetProperty("total").GetProperty("total").GetInt64());
        Assert.Equal("openai/test-model", usage.GetProperty("by_role").GetProperty("developer").GetProperty("model").GetString());
        Assert.Contains("| developer | openai/test-model | 2 | 110 | 22 |", Git(origin, "show", $"{result.Branch}:{Directory}/README.md"));
        Assert.Contains("- model: openai/test-model", Git(origin, "show", $"{result.Branch}:{Directory}/001-developer.md"));
    }

    [Fact]
    public async Task A_record_without_any_usage_has_no_usage_file()
    {
        var workspace = Workspace();
        await PrepareWithChange(workspace);
        await workspace.RecordAsync(Step(1, "analyst", "p", "o"));

        var result = await workspace.PublishAsync(new PublishRequest(Name, "Add things", Spec()), CancellationToken.None);

        Assert.DoesNotContain($"{Directory}/usage.json", Tree(result!.Branch));
    }

    [Fact]
    public async Task Without_a_record_request_nothing_is_added_even_if_steps_were_stored()
    {
        var workspace = Workspace();
        await PrepareWithChange(workspace);
        await workspace.RecordAsync(Step(1, "analyst", "p", "o"));

        var result = await workspace.PublishAsync(new PublishRequest(Name, "Add things"), CancellationToken.None);

        Assert.DoesNotContain(Tree(result!.Branch), path => path.StartsWith(".factory/runs"));
    }

    [Fact]
    public async Task A_credential_in_a_prompt_or_answer_never_reaches_the_branch()
    {
        var workspace = Workspace(token: "factory-token-0123456789");
        await PrepareWithChange(workspace);
        await workspace.RecordAsync(Step(1, "developer", "use factory-token-0123456789 please", "api_key = \"abcd1234efgh5678ijkl\"\nfine"));

        var result = await workspace.PublishAsync(new PublishRequest(Name, "Add things", Spec()), CancellationToken.None);

        var file = Git(origin, "show", $"{result!.Branch}:{Directory}/001-developer.md");
        Assert.DoesNotContain("factory-token-0123456789", file);
        Assert.DoesNotContain("abcd1234efgh5678ijkl", file);
        Assert.Contains("fine", file);
    }

    [Fact]
    public async Task A_work_in_progress_branch_gets_the_record_too()
    {
        var workspace = Workspace();
        await PrepareWithChange(workspace);
        await workspace.RecordAsync(Step(1, "developer", "Implement it", "gave up"));

        var result = await workspace.RescueAsync(
            new RescueRequest(Name, "WIP: add things", new RunRecordSpec(Directory, "acme/shop#1", "Stopped before finishing (build_failed).")), "http://ui/run", CancellationToken.None);

        Assert.True(result.Saved);
        Assert.Contains($"{Directory}/001-developer.md", Tree(result.Branch!));
        Assert.Contains("Stopped before finishing (build_failed).", Git(origin, "show", $"{result.Branch}:{Directory}/README.md"));
    }

    [Fact]
    public async Task A_publish_that_is_repeated_writes_the_same_record_again_without_duplicates()
    {
        var workspace = Workspace();
        await PrepareWithChange(workspace);
        await workspace.RecordAsync(Step(1, "analyst", "p", "o"));

        var first = await workspace.PublishAsync(new PublishRequest(Name, "Add things", Spec()), CancellationToken.None);
        var second = await workspace.PublishAsync(new PublishRequest(Name, "Add things", Spec()), CancellationToken.None);

        Assert.Equal(first!.Branch, second!.Branch);
        Assert.Single(Tree(second.Branch), path => path.EndsWith("001-analyst.md"));
    }

    [Theory]
    [InlineData(".factory/runs/../../app.py")]
    [InlineData(".github/workflows")]
    [InlineData("app.py")]
    public async Task A_record_location_outside_the_runs_folder_is_refused(string directory)
    {
        var workspace = Workspace();
        await PrepareWithChange(workspace);
        await workspace.RecordAsync(Step(1, "analyst", "p", "o"));

        var error = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() =>
            workspace.PublishAsync(new PublishRequest(Name, "Add things", new RunRecordSpec(directory, "h", "o")), CancellationToken.None));

        Assert.Contains("run record location", error.Message);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("Analyst")]
    [InlineData("a b")]
    public async Task A_step_with_an_unsafe_name_is_refused(string slug)
    {
        var workspace = Workspace();
        await PrepareWithChange(workspace);

        await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() => workspace.RecordAsync(Step(1, slug, "p", "o")));
    }

    [Fact]
    public async Task Stored_steps_are_removed_with_the_checkout()
    {
        var workspace = Workspace();
        await PrepareWithChange(workspace);
        await workspace.RecordAsync(Step(1, "analyst", "p", "o"));
        Assert.True(System.IO.Directory.Exists($"{root}/.records/{Name}"));

        await workspace.CleanupAsync(Name);

        Assert.False(System.IO.Directory.Exists($"{root}/.records/{Name}"));
    }
}

[Collection("temporal")]
public sealed class RecordScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private const string RecordingProject =
        """{"image":"python","record":true,"gates":[{"name":"test","command":["python","-m","pytest","-q"]}]}""";

    private static FakeWorld Building(string? project) => new FakeWorld { ProjectConfig = project }
        .Agent("analyst", Say(Answers.Analyst()))
        .Agent("coverage", Say(Answers.Coverage("missing", "missing")));

    private static FakeWorld ToPullRequest(string? project) => Building(project)
        .Agent("developer", Say(Answers.Developer()))
        .Agent("reviewer", Say(Answers.Review()))
        .Agent("qa", Say(Answers.Qa("met", "met")));

    [SkippableFact]
    public async Task A_recording_project_keeps_every_prompt_answer_and_check_in_order()
    {
        var world = ToPullRequest(RecordingProject);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(["analyst", "coverage", "developer", "gate-test", "reviewer", "qa"], world.Records.Select(r => r.Slug));
        Assert.Equal([1, 2, 3, 4, 5, 6], world.Records.Select(r => r.Sequence));
        Assert.All(world.Records, record => Assert.Contains(record.Outcome, new[] { "accepted", "passed" }));
        var analyst = world.Records[0];
        Assert.Equal(world.Calls("analyst").Single().Prompt, analyst.Input);
        Assert.Contains("\"verdict\"", analyst.Output);
        var gate = world.Records[3];
        Assert.Equal("python -m pytest -q", gate.Input);
        Assert.Equal("passed", gate.Outcome);
        // Agents carry what OpenCode reported for the run; a check has no model usage.
        Assert.All(world.Records.Where(r => r.Kind == "agent"), r => Assert.Equal(new TokenUsage(100, 20, 5, 50, 0, 175, 0.01, 1), r.Usage));
        Assert.Null(gate.Usage);
    }

    [SkippableFact]
    public async Task The_publish_asks_for_the_record_in_a_folder_for_this_ticket_and_run()
    {
        var world = ToPullRequest(RecordingProject);

        await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        var spec = Assert.Single(world.Publishes).Record;
        Assert.NotNull(spec);
        Assert.Matches(@"^\.factory/runs/issue-1-[a-z0-9]{8}$", spec.Directory);
        Assert.Contains("Published as a pull request", spec.Outcome);
        Assert.Matches(@"^acme/r[0-9a-f]{10}#1: Add goodbye$", spec.Heading);
    }

    [SkippableFact]
    public async Task A_project_that_does_not_ask_records_nothing()
    {
        var world = ToPullRequest("""{"image":"python","gates":[{"name":"test","command":["python","-m","pytest","-q"]}]}""");

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Empty(world.Records);
        Assert.Null(Assert.Single(world.Publishes).Record);
    }

    [SkippableFact]
    public async Task An_answer_the_factory_rejected_is_recorded_too_so_the_retry_makes_sense()
    {
        var world = Building(RecordingProject)
            .Agent("developer", Say("not json at all"), Say(Answers.Developer()))
            .Agent("reviewer", Say(Answers.Review()))
            .Agent("qa", Say(Answers.Qa("met", "met")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("pr_opened", result.Status);
        var developer = world.Records.Where(r => r.Slug == "developer").ToList();
        Assert.Equal(["answer rejected", "accepted"], developer.Select(r => r.Outcome));
        Assert.Equal([1, 2], developer.Select(r => r.Attempt));
        Assert.Equal("not json at all", developer[0].Output);
        Assert.NotNull(developer[0].Problem);
        Assert.Contains("could not be used", developer[1].Input); // the second prompt says what was wrong
    }

    [SkippableFact]
    public async Task A_failed_check_is_recorded_with_its_output_and_the_wip_branch_asks_for_the_record()
    {
        var world = Building(RecordingProject)
            .Agent("developer", Say(Answers.Developer()), Say(Answers.Developer()), Say(Answers.Developer()))
            .Check("python -m pytest -q", FakeWorld.Fails("2 failed, 1 passed"), FakeWorld.Fails("still red"), FakeWorld.Fails("still red"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(ScenarioContext.Request()));

        Assert.Equal("build_failed", result.Status);
        var gates = world.Records.Where(r => r.Kind == "gate").ToList();
        Assert.Equal(3, gates.Count);
        Assert.All(gates, gate => Assert.Equal("failed", gate.Outcome));
        Assert.Equal("2 failed, 1 passed", gates[0].Output);
        var spec = Assert.Single(world.Rescues).Record;
        Assert.NotNull(spec);
        Assert.Contains("build_failed", spec.Outcome);
        Assert.Matches(@"^\.factory/runs/issue-1-[a-z0-9]{8}$", spec.Directory);
    }
}
