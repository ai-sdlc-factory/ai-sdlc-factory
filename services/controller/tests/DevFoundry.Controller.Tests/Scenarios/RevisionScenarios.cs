using System.Text.Json;

namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>A person reviews a pull request the factory opened: the resolver addresses the comments on its branch, the checks run again, and one commit is pushed.</summary>
[Collection("temporal")]
public sealed class RevisionScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static FakeWorld Reviewing(params string[] resolverAnswers) => new FakeWorld()
        .Agent("pr-resolver", (resolverAnswers.Length == 0 ? [Answers.Revision("done", "fixed", "fixed", "fixed")] : resolverAnswers).Select(Say).ToArray())
        .Agent("reviewer", Say(Answers.Review()));

    private static string[] Roles(FakeWorld world) => world.Snapshot(world.AgentCalls).Select(call => call.Role).ToArray();

    [SkippableFact]
    public async Task A_review_is_addressed_pushed_onto_the_same_branch_and_answered_on_the_pull_request()
    {
        var world = Reviewing(Answers.Revision("done", "fixed", "answered", "fixed"));
        var request = ScenarioContext.Revision();

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(request));

        Assert.Equal("revision_pushed", result.Status);
        Assert.Equal(["pr-resolver", "reviewer"], Roles(world));
        Assert.Empty(world.Problems);
        var prepare = Assert.Single(world.PrepareRequests);
        Assert.True(prepare.ExistingBranch);
        Assert.Equal("factory/issue-7", prepare.Branch);
        var publish = Assert.Single(world.Publishes);
        Assert.True(publish.UpdateExistingBranch);
        Assert.StartsWith("Address review comments on #7", publish.Message);
        Assert.Contains(world.Comments, c => c.StartsWith("I am working on the 3 review comments"));
        var reply = Assert.Single(world.Comments, c => c.Contains(RevisionReply.Marker));
        Assert.Contains("**Revision pushed: `c0ffee`**", reply);
        Assert.Contains("| 2 | `hello.py:4` Rename this function. | answered: Reason for comment 2. |", reply);
        Assert.Contains("A person should look at the new commit", reply);
        Assert.Equal([request.WorkflowId], world.CleanedUp);
    }

    [SkippableFact]
    public async Task The_resolver_is_given_every_comment_numbered_and_works_in_a_writable_workspace_with_package_access()
    {
        var world = Reviewing();

        await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        var call = Assert.Single(world.Calls("pr-resolver"));
        Assert.True(call.Writable);
        Assert.Equal(SandboxNetworks.Developer, call.Network);
        Assert.Contains("[1] bob on general:", call.Prompt);
        Assert.Contains("[2] bob on hello.py:4:", call.Prompt);
        Assert.Contains("Rename this function.", call.Prompt);
        Assert.Contains("[3] bob on test_hello.py:9:", call.Prompt);
        Assert.Contains("- test: python -m pytest -q", call.Prompt);
    }

    [SkippableFact]
    public async Task Reviewers_look_at_the_revision_but_QA_is_not_used()
    {
        var world = Reviewing();
        world.ProjectConfig = """{"image":"python","reviewers":["reviewer","security"],"gates":[{"name":"test","command":["python","-m","pytest","-q"]}]}""";
        world.Agent("security", Say(Answers.Review()));

        await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal(["pr-resolver", "reviewer", "security"], Roles(world));
        Assert.Contains("Review comments on pull request #7: Add goodbye", world.Calls("reviewer").Single().Prompt);
    }

    [SkippableFact]
    public async Task When_every_comment_is_answered_or_declined_nothing_is_pushed_and_the_answers_are_the_result()
    {
        var world = Reviewing(Answers.Revision("done", "answered", "declined", "answered")).Changes(new ChangeReport([], []));

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("revision_replied", result.Status);
        Assert.Empty(world.Publishes);
        Assert.Empty(world.CheckCalls); // no change, so no checks
        Assert.Equal(["pr-resolver"], Roles(world));
        var reply = Assert.Single(world.Comments, c => c.Contains(RevisionReply.Marker));
        Assert.Contains("**Revision: no code change**", reply);
        Assert.Contains("declined: Reason for comment 2.", reply);
    }

    [SkippableFact]
    public async Task A_fix_claimed_without_any_changed_file_goes_back_to_the_resolver()
    {
        var world = Reviewing(Answers.Revision("done", "fixed", "fixed", "fixed"), Answers.Revision("done", "answered", "answered", "answered"))
            .Changes(new ChangeReport([], []), new ChangeReport([], []));

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("revision_replied", result.Status);
        Assert.Contains("You marked comment 1, 2, 3 as fixed, but no files in the workspace changed", world.Calls("pr-resolver").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task Failing_checks_go_back_to_the_resolver_and_the_second_round_is_pushed()
    {
        var world = Reviewing(Answers.Revision("done", "fixed", "fixed", "fixed"), Answers.Revision("done", "fixed", "fixed", "fixed"))
            .Agent("reviewer", Say(Answers.Review()))
            .Check("python -m pytest -q", FakeWorld.Fails("1 failed: assert 1 == 2"), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("revision_pushed", result.Status);
        Assert.Contains("1 failed: assert 1 == 2", world.Calls("pr-resolver").ElementAt(1).Prompt);
        Assert.Equal(2, world.CheckCalls.Count);
        Assert.Single(world.Publishes);
    }

    [SkippableFact]
    public async Task A_blocking_review_finding_goes_back_to_the_resolver()
    {
        var world = Reviewing(Answers.Revision("done", "fixed", "fixed", "fixed"), Answers.Revision("done", "fixed", "fixed", "fixed"))
            .Agent("reviewer", Say(Answers.Review(("major", "The rename misses one caller."))), Say(Answers.Review()));

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("revision_pushed", result.Status);
        Assert.Contains("[reviewer, major] hello.py: The rename misses one caller.", world.Calls("pr-resolver").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task Three_rounds_that_fail_push_nothing_to_the_pull_request_and_save_the_work_aside()
    {
        var failing = Enumerable.Repeat(FakeWorld.Fails("still red"), 3).ToArray();
        var world = Reviewing(Enumerable.Repeat(Answers.Revision("done", "fixed", "fixed", "fixed"), 3).ToArray()).Check("python -m pytest -q", failing);

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("build_failed", result.Status);
        Assert.Empty(world.Publishes);
        Assert.Contains(world.Comments, c => c.Contains("nothing was pushed to this pull request") && c.Contains("A person needs to take over"));
        Assert.Single(world.Rescues); // what was written is saved to a -wip branch, like for a feature
    }

    [SkippableFact]
    public async Task An_answer_that_leaves_a_comment_out_is_sent_back_naming_it()
    {
        var world = Reviewing(Answers.Revision("done", "fixed"), Answers.Revision("done", "fixed", "answered", "fixed"));

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("revision_pushed", result.Status);
        Assert.Contains("comment 2, 3 have no response; answer every comment", world.Calls("pr-resolver").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task A_resolver_that_cannot_continue_stops_with_its_reasons()
    {
        var world = Reviewing(Answers.Revision("blocked"));

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("build_blocked", result.Status);
        Assert.Empty(world.Publishes);
        Assert.Contains(world.Comments, c => c.Contains("**Revision: I cannot continue**") && c.Contains("Cannot continue."));
    }

    [SkippableFact]
    public async Task A_pull_request_the_factory_did_not_open_is_left_alone_without_a_word()
    {
        var world = Reviewing();
        world.RevisionContext = FakeWorld.FactoryPullRequest(headRef: "feature/my-change");

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("ignored", result.Status);
        Assert.Empty(world.Comments);
        Assert.Empty(world.PrepareRequests);
        Assert.Empty(world.AgentCalls);
    }

    [SkippableFact]
    public async Task A_pull_request_that_cannot_be_read_is_reported_to_the_person_who_asked()
    {
        var world = Reviewing();
        world.RevisionContextFailure = new Temporalio.Exceptions.ApplicationFailureException("Forgejo rejected the pull request: 403 Forbidden", nonRetryable: true);

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("error", result.Status);
        Assert.Contains("I could not read this pull request, so I did nothing: Forgejo rejected the pull request: 403 Forbidden", Assert.Single(world.Comments));
        Assert.Empty(world.PrepareRequests);
    }

    [SkippableFact]
    public async Task A_closed_pull_request_is_not_changed_and_says_so()
    {
        var world = Reviewing();
        world.RevisionContext = FakeWorld.FactoryPullRequest(state: "closed");

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("ignored", result.Status);
        Assert.Equal([TicketComments.RevisionClosed], world.Comments);
        Assert.Empty(world.PrepareRequests);
    }

    [SkippableFact]
    public async Task After_three_revisions_a_person_is_asked_to_take_over()
    {
        var world = Reviewing();
        world.RevisionContext = FakeWorld.FactoryPullRequest(prior: 3);

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        Assert.Equal("revision_limit", result.Status);
        Assert.Contains("already made 3 revisions", Assert.Single(world.Comments));
        Assert.Empty(world.AgentCalls);
    }

    [SkippableFact]
    public async Task A_command_with_nothing_to_act_on_says_what_to_write()
    {
        var world = Reviewing();
        world.RevisionContext = FakeWorld.FactoryPullRequest(noReview: true);

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision(RevisionRequest.ByCommand, instruction: "")));

        Assert.Equal("nothing_to_do", result.Status);
        Assert.Contains("Write what to change after `/fix`", Assert.Single(world.Comments));
    }

    [SkippableFact]
    public async Task A_command_with_text_works_without_any_review()
    {
        var world = Reviewing(Answers.Revision("done", "fixed"));
        world.RevisionContext = FakeWorld.FactoryPullRequest(noReview: true);

        var result = await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision(RevisionRequest.ByCommand, "Use a constant for the greeting.")));

        Assert.Equal("revision_pushed", result.Status);
        Assert.Contains("[1] bob on general:", world.Calls("pr-resolver").Single().Prompt);
        Assert.Contains("Use a constant for the greeting.", world.Calls("pr-resolver").Single().Prompt);
    }

    [SkippableFact]
    public async Task The_revision_is_recorded_in_its_own_folder_when_the_project_keeps_a_record()
    {
        var world = Reviewing();
        world.ProjectConfig = """{"image":"python","record":true,"gates":[{"name":"test","command":["python","-m","pytest","-q"]}]}""";

        await world.RunAsync(fixture, context => context.RunRevisionAsync(ScenarioContext.Revision()));

        var record = Assert.Single(world.Publishes).Record;
        Assert.Matches(@"^\.factory/runs/pr-7-[a-z0-9]{8}$", record!.Directory);
        Assert.Contains(world.Records, r => r.Slug == "pr-resolver");
    }

    // ---- through the router ---------------------------------------------------------------------------------------------

    private static ForgejoEvent PullComment(string repo, string body) =>
        new(Guid.NewGuid().ToString(), "issue_comment", JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            action = "created",
            repository = new { full_name = $"acme/{repo}" },
            issue = new { number = 7, title = "Add goodbye", body = "Closes #3", state = "open", user = new { login = "factory-bot" }, labels = Array.Empty<object>(), pull_request = new { merged = false } },
            comment = new { body, user = new { login = "bob" } },
            is_pull = true,
        })).RootElement);

    private static Task Route(ScenarioContext context, ForgejoEvent input) =>
        context.Client.StartWorkflowAsync(
            (ForgejoEventWorkflow workflow) => workflow.RunAsync(input),
            new Temporalio.Client.WorkflowOptions($"forgejo-{input.DeliveryId}", context.Queue)).Result.GetResultAsync();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition not reached in time");
            await Task.Delay(50);
        }
    }

    [SkippableFact]
    public async Task The_fix_command_on_a_pull_request_starts_a_revision_through_the_router()
    {
        var world = Reviewing(Answers.Revision("done", "fixed", "fixed", "fixed", "fixed")); // the text after /fix plus the review's three comments
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, PullComment(repo, "/fix rename it"));
            await world.WaitForCommentAsync(c => c.Contains(RevisionReply.Marker)); // the reply comes just after the push
            return 0;
        });

        Assert.Contains(world.Comments, c => c.Contains(RevisionReply.Marker));
        Assert.Single(world.Calls("pr-resolver"));
    }

    [SkippableFact]
    public async Task A_second_fix_while_a_revision_is_running_gets_a_notice_and_does_not_start_another()
    {
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var world = Reviewing(Answers.Revision("done", "fixed", "fixed", "fixed", "fixed"));
        world.Prepare = request =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return new WorkspaceInfo(request.Name, $"/work/{request.Name}", request.Branch, "main", "abcdef1234567890");
        };
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, PullComment(repo, "/fix first"));
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
            await Route(context, PullComment(repo, "/fix second"));
            await WaitUntilAsync(() => world.Snapshot(world.Notices).Count == 1);
            release.Set();
            await WaitUntilAsync(() => world.Snapshot(world.Publishes).Count == 1);
            return 0;
        });

        Assert.Equal(TicketComments.RevisionBusy, Assert.Single(world.Notices));
        Assert.Single(world.Calls("pr-resolver")); // only the first revision ran
    }

    [SkippableFact]
    public async Task A_finished_revision_can_be_followed_by_another_on_the_same_pull_request()
    {
        var four = Answers.Revision("done", "fixed", "fixed", "fixed", "fixed");
        var world = Reviewing(four, four).Agent("reviewer", Say(Answers.Review()), Say(Answers.Review()));
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, PullComment(repo, "/fix first"));
            await WaitUntilAsync(() => world.Snapshot(world.Publishes).Count == 1);
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 1);
            await Route(context, PullComment(repo, "/fix second"));
            await WaitUntilAsync(() => world.Snapshot(world.Publishes).Count == 2);
            return 0;
        });

        Assert.Equal(2, world.Calls("pr-resolver").Count());
        Assert.Empty(world.Snapshot(world.Notices));
    }

    [SkippableFact]
    public async Task A_plain_comment_on_a_pull_request_starts_nothing()
    {
        var world = Reviewing();
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, PullComment(repo, "Looks fine to me, thanks."));
            await Task.Delay(1500);
            return 0;
        });

        Assert.Empty(world.AgentCalls);
        Assert.Empty(world.PrepareRequests);
        Assert.Empty(world.Notices);
    }
}
