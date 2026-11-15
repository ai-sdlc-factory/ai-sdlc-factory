namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>/ask: the asker reads the repository and the answer is posted. Nothing is written, pushed or run.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class AskScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static FakeWorld Asking(params string[] answers) =>
        new FakeWorld().Agent("asker", (answers.Length == 0 ? [Answers.Ask(files: "hello.py")] : answers).Select(Say).ToArray());

    [SkippableFact]
    public async Task A_question_is_answered_on_the_ticket_and_nothing_else_happens()
    {
        var world = Asking(Answers.Ask(answer: "The greeting is built in `hello.py`.", files: ["hello.py"]));
        var request = ScenarioContext.Ask();

        var result = await world.RunAsync(fixture, context => context.RunAskAsync(request));

        Assert.Equal("answered", result.Status);
        Assert.Empty(world.Problems);
        Assert.Contains(world.Comments, c => c.StartsWith("I am reading the repository to answer your question. I only read: nothing will be changed."));
        var answer = Assert.Single(world.Comments, c => c.StartsWith("**Answer**"));
        Assert.Contains("hello.py", answer);
        Assert.Contains("Files read: `hello.py`", answer);
        Assert.Empty(world.Publishes);
        Assert.Empty(world.PullRequests);
        Assert.Empty(world.Rescues);
        Assert.Equal([request.WorkflowId], world.CleanedUp);
    }

    [SkippableFact]
    public async Task The_asker_cannot_edit_or_run_anything_and_has_no_package_access()
    {
        var world = Asking();

        await world.RunAsync(fixture, context => context.RunAskAsync(ScenarioContext.Ask()));

        var call = Assert.Single(world.Calls("asker"));
        Assert.False(call.Writable);
        Assert.Equal(SandboxNetworks.Model, call.Network);
        Assert.Contains("Where is the greeting built?", call.Prompt);
        var prepare = Assert.Single(world.PrepareRequests);
        Assert.False(prepare.ExistingBranch);
        Assert.Equal("factory/ask-7", prepare.Branch);
    }

    [SkippableFact]
    public async Task A_question_on_a_pull_request_is_given_what_the_pull_request_changes()
    {
        var world = Asking();

        await world.RunAsync(fixture, context => context.RunAskAsync(ScenarioContext.Ask("Does this break anything?", onPullRequest: true)));

        var call = Assert.Single(world.Calls("asker"));
        Assert.Contains("hello.py", call.Prompt);
        Assert.Contains("<<<DIFF", call.Prompt);
        Assert.Contains("def goodbye()", call.Prompt);
        Assert.Contains(world.Comments, c => c.Contains("and what this pull request changes"));
    }

    [SkippableFact]
    public async Task A_pull_request_that_cannot_be_read_still_gets_an_answer_from_the_repository()
    {
        var world = Asking();
        world.ReviewContextFailure = new Temporalio.Exceptions.ApplicationFailureException("Forgejo is down");

        var result = await world.RunAsync(fixture, context => context.RunAskAsync(ScenarioContext.Ask(onPullRequest: true)));

        Assert.Equal("answered", result.Status);
        Assert.DoesNotContain("<<<DIFF", Assert.Single(world.Calls("asker")).Prompt);
    }

    [SkippableFact]
    public async Task Files_the_agent_names_but_that_do_not_exist_are_not_shown_as_read()
    {
        var world = Asking(Answers.Ask(answer: "See `hello.py` and `made/up.py`.", files: ["hello.py", "made/up.py"]));
        world.ExistingFiles = ["hello.py"];

        await world.RunAsync(fixture, context => context.RunAskAsync(ScenarioContext.Ask()));

        var answer = Assert.Single(world.Comments, c => c.StartsWith("**Answer**"));
        Assert.Contains("Files read: `hello.py`", answer);
        Assert.DoesNotContain("Files read: `hello.py`, `made/up.py`", answer);
    }

    [SkippableFact]
    public async Task When_the_repository_does_not_say_the_comment_says_so()
    {
        var world = Asking(Answers.Ask("cannot_answer", "It depends on how it is deployed; the repository has no deployment files."));

        var result = await world.RunAsync(fixture, context => context.RunAskAsync(ScenarioContext.Ask("Where is production?")));

        Assert.Equal("no_answer", result.Status);
        Assert.Contains(world.Comments, c => c.StartsWith("**I could not answer this from the repository**"));
    }

    [SkippableFact]
    public async Task A_bare_ask_is_told_how_to_use_it_and_no_agent_runs()
    {
        var world = Asking();

        var result = await world.RunAsync(fixture, context => context.RunAskAsync(ScenarioContext.Ask("")));

        Assert.Equal("ignored", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("Write your question after `/ask`"));
        Assert.Empty(world.Snapshot(world.AgentCalls));
        Assert.Empty(world.PrepareRequests);
    }

    [SkippableFact]
    public async Task An_unusable_answer_is_asked_for_again_once()
    {
        var world = Asking("not json at all", Answers.Ask(answer: "It starts in `hello.py`.", files: ["hello.py"]));

        var result = await world.RunAsync(fixture, context => context.RunAskAsync(ScenarioContext.Ask()));

        Assert.Equal("answered", result.Status);
        Assert.Equal(2, world.Calls("asker").Count());
    }

    [SkippableFact]
    public async Task An_agent_that_keeps_failing_ends_with_a_comment_and_a_failed_status()
    {
        var world = Asking("nope", "still nope");

        var result = await world.RunAsync(fixture, context => context.RunAskAsync(ScenarioContext.Ask()));

        Assert.Equal("ask_failed", result.Status);
        Assert.Contains(world.Comments, c => c.StartsWith("I could not answer:"));
        Assert.DoesNotContain(world.Comments, c => c.StartsWith("**Answer**"));
    }

    [SkippableFact]
    public async Task A_project_without_settings_is_told_to_add_them()
    {
        var world = Asking();
        world.ProjectConfig = null;

        var result = await world.RunAsync(fixture, context => context.RunAskAsync(ScenarioContext.Ask()));

        Assert.Equal("needs_config", result.Status);
        Assert.Empty(world.Snapshot(world.AgentCalls));
    }
}
