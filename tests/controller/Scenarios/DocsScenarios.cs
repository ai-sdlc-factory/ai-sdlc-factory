namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>A ticket labelled docs: the writer writes or updates AGENTS.md and the documents beside it, the reviewers check them, and a pull request is opened.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class DocsScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static WorkRequest Ticket(string body = "Write AGENTS.md for new contributors.") =>
        ScenarioContext.Request(title: "Onboarding documents", body: body, labels: ["docs"]) with { Kind = WorkIds.Docs };

    private static ChangeReport Wrote(params string[] paths) =>
        new(paths.Select(path => new FileChange(path, FileChange.Added)).ToList(), []);

    private static FakeWorld Writing(params string[] writerAnswers) => new FakeWorld()
        .Agent("docs-writer", (writerAnswers.Length == 0 ? [Answers.Docs()] : writerAnswers).Select(Say).ToArray())
        .Agent("reviewer", Say(Answers.Review()));

    private static string[] Roles(FakeWorld world) => world.Snapshot(world.AgentCalls).Select(call => call.Role).ToArray();

    [SkippableFact]
    public async Task The_documents_are_written_reviewed_and_opened_as_a_pull_request()
    {
        var world = Writing().Changes(Wrote("AGENTS.md", "docs/architecture.md"));
        var request = Ticket();

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(request));

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(["docs-writer", "reviewer"], Roles(world));
        Assert.Empty(world.Problems);
        var writer = Assert.Single(world.Calls("docs-writer"));
        Assert.True(writer.Writable);
        Assert.Equal(SandboxNetworks.Model, writer.Network); // documents need no package registries
        Assert.Contains("Write AGENTS.md for new contributors.", writer.Prompt);
        Assert.Empty(world.CheckCalls); // no code changed, so the project's code checks are not run
        var publish = Assert.Single(world.Publishes);
        Assert.StartsWith("Docs: Onboarding documents", publish.Message);
        var pullRequest = Assert.Single(world.PullRequests);
        Assert.Equal("Docs: Onboarding documents", pullRequest.Title);
        Assert.StartsWith("Refs #1", pullRequest.Body);
        Assert.Contains("`AGENTS.md`, `docs/architecture.md`", pullRequest.Body);
        Assert.Contains("A person must read it", pullRequest.Body);
        Assert.Contains(world.Comments, c => c.Contains("**Pull request opened:**") && c.Contains("2 documentation files"));
        Assert.Equal([request.WorkflowId], world.CleanedUp);
    }

    [SkippableFact]
    public async Task A_ticket_with_no_text_tells_the_writer_to_write_agents_md_or_bring_it_up_to_date()
    {
        var world = Writing().Changes(Wrote("AGENTS.md"));

        await world.RunAsync(fixture, context => context.RunToEndAsync(Ticket(body: "")));

        Assert.Contains("write AGENTS.md if the repository has none, otherwise bring AGENTS.md and README.md up to date", Assert.Single(world.Calls("docs-writer")).Prompt);
    }

    [SkippableFact]
    public async Task A_change_to_any_other_file_goes_back_to_the_writer_with_the_files_it_may_write()
    {
        var world = Writing(Answers.Docs(), Answers.Docs())
            .Changes(Wrote("AGENTS.md", "src/app.py"), Wrote("AGENTS.md"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Ticket()));

        Assert.Equal("pr_opened", result.Status);
        var second = world.Calls("docs-writer").ElementAt(1).Prompt;
        Assert.Contains("You may only add or change AGENTS.md, README.md, CONTRIBUTING.md and Markdown files under docs/", second);
        Assert.Contains("added src/app.py", second);
    }

    [SkippableFact]
    public async Task A_deleted_document_is_not_allowed()
    {
        var world = Writing(Answers.Docs(), Answers.Docs())
            .Changes(new ChangeReport([new FileChange("README.md", FileChange.Deleted)], []), Wrote("README.md"));

        await world.RunAsync(fixture, context => context.RunToEndAsync(Ticket()));

        Assert.Contains("deleted README.md", world.Calls("docs-writer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task A_protected_file_or_other_violation_goes_back_to_the_writer()
    {
        var world = Writing(Answers.Docs(), Answers.Docs())
            .Changes(FakeWorld.Rejected(".factory/project.json is a protected file that only a person may change"), Wrote("AGENTS.md"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Ticket()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("protected file", world.Calls("docs-writer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task Documents_that_are_already_right_open_no_pull_request()
    {
        var world = Writing(Answers.Docs("unchanged")).Changes(new ChangeReport([], []));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Ticket()));

        Assert.Equal("no_changes", result.Status);
        Assert.Empty(world.Publishes);
        Assert.Empty(world.PullRequests);
        Assert.Equal(["docs-writer"], Roles(world));
        Assert.Contains(world.Comments, c => c.Contains("**Documentation: nothing to change**"));
    }

    [SkippableFact]
    public async Task Done_with_no_changed_file_goes_back_to_the_writer()
    {
        var world = Writing(Answers.Docs(), Answers.Docs())
            .Changes(new ChangeReport([], []), Wrote("AGENTS.md"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Ticket()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("You answered done, but no file in the workspace changed", world.Calls("docs-writer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task A_writer_that_cannot_continue_ends_the_run_with_its_reason()
    {
        var world = Writing(Answers.Docs("blocked"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Ticket()));

        Assert.Equal("build_blocked", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("**Documentation: I cannot continue**") && c.Contains("Cannot continue."));
        Assert.Empty(world.Publishes);
    }

    [SkippableFact]
    public async Task A_blocking_review_finding_goes_back_to_the_writer_and_the_second_round_is_published()
    {
        var world = Writing(Answers.Docs(), Answers.Docs())
            .Agent("reviewer", Say(Answers.Review(("major", "AGENTS.md names a test command that does not exist."))), Say(Answers.Review()))
            .Changes(Wrote("AGENTS.md"), Wrote("AGENTS.md"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Ticket()));

        Assert.Equal("pr_opened", result.Status);
        Assert.Contains("AGENTS.md names a test command that does not exist.", world.Calls("docs-writer").ElementAt(1).Prompt);
        Assert.Single(world.Publishes);
    }

    [SkippableFact]
    public async Task Rounds_that_keep_failing_publish_nothing()
    {
        var world = Writing(Enumerable.Repeat(Answers.Docs(), 3).ToArray())
            .Agent("reviewer", Enumerable.Repeat(Say(Answers.Review(("major", "Still wrong."))), 3).ToArray())
            .Changes(Wrote("AGENTS.md"), Wrote("AGENTS.md"), Wrote("AGENTS.md"));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Ticket()));

        Assert.Equal("build_failed", result.Status);
        Assert.Empty(world.PullRequests);
    }

    [SkippableFact]
    public async Task A_writer_that_is_not_signed_in_blocks_the_run()
    {
        var world = new FakeWorld().Agent("docs-writer", AgentReply.NotSignedIn);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Ticket()));

        Assert.Equal("blocked", result.Status);
    }

    [SkippableFact]
    public async Task A_pull_request_that_cannot_be_opened_names_the_branch()
    {
        var world = Writing().Changes(Wrote("AGENTS.md"));
        world.PullRequestFailure = new Temporalio.Exceptions.ApplicationFailureException("The host rejected the pull request: 422", nonRetryable: true);

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Ticket()));

        Assert.Equal("publish_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("opening the pull request failed") && c.Contains("factory/issue-1"));
    }
}
