namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>A person asks for the factory's pull request to be brought up to date: the base branch is merged, conflicts are resolved, the checks run again, and one merge commit is pushed.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class UpdateScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static readonly ChangeReport NothingChanged = new([], []);

    private static MergeResult Conflicting(params string[] paths) =>
        new(MergeResult.Conflicted, "b4a5e6f7c8d90123", paths.Select(path => new ConflictedFile(path, "both changed it")).ToList(), 4);

    private static FakeWorld Conflicted(params string[] paths) => new FakeWorld { Merge = Conflicting(paths) }
        .Agent("merge-resolver", Say(Answers.Merge("done", paths)))
        .Agent("reviewer", Say(Answers.Review()));

    private static string[] Roles(FakeWorld world) => world.Snapshot(world.AgentCalls).Select(call => call.Role).ToArray();

    private static string Reply(FakeWorld world) => Assert.Single(world.Comments, c => c.Contains(UpdateReply.Marker));

    [SkippableFact]
    public async Task A_clean_merge_is_checked_and_pushed_without_asking_any_agent()
    {
        var world = new FakeWorld().Changes(NothingChanged);
        var request = ScenarioContext.Update();

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(request));

        Assert.Equal("update_pushed", result.Status);
        Assert.Empty(world.AgentCalls);
        Assert.Empty(world.Problems);
        var prepare = Assert.Single(world.PrepareRequests);
        Assert.True(prepare.ExistingBranch);
        Assert.Equal("factory/issue-7", prepare.Branch);
        Assert.Equal("main", Assert.Single(world.MergeRequests).BaseBranch);
        Assert.Equal(["python -m pytest -q"], world.Snapshot(world.CheckCalls)); // the merged tree is tested
        var publish = Assert.Single(world.MergePublishes);
        Assert.StartsWith("Merge branch 'main' into factory/issue-7", publish.Message);
        Assert.Empty(world.Publishes); // not a plain publish
        var reply = Reply(world);
        Assert.Contains("**Updated from `main`: `deadbeef`**", reply);
        Assert.Contains("3 files came in", reply);
        Assert.DoesNotContain(RevisionReply.Marker, reply); // an update is not a revision
        Assert.Equal("factory/issue-7", result.WorkBranch);
        Assert.Equal([request.WorkflowId], world.CleanedUp);
    }

    [SkippableFact]
    public async Task A_branch_that_already_has_the_base_is_left_alone()
    {
        var world = new FakeWorld { Merge = new MergeResult(MergeResult.UpToDate, "b4a5e6f7c8d90123", []) };

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("up_to_date", result.Status);
        Assert.Empty(world.MergePublishes);
        Assert.Empty(world.CheckCalls);
        Assert.Empty(world.AgentCalls);
        Assert.Contains("**Already up to date**", Reply(world));
    }

    [SkippableFact]
    public async Task Conflicts_go_to_the_resolver_then_the_checks_and_the_reviewers_and_one_merge_is_pushed()
    {
        var world = Conflicted("src/app.py", "README.md");

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("update_pushed", result.Status);
        Assert.Equal(["merge-resolver", "reviewer"], Roles(world));
        Assert.Empty(world.Problems);
        var call = Assert.Single(world.Calls("merge-resolver"));
        Assert.True(call.Writable);
        Assert.Equal(SandboxNetworks.Developer, call.Network);
        Assert.Contains("- src/app.py: both changed it", call.Prompt);
        Assert.Contains("- README.md: both changed it", call.Prompt);
        Assert.Contains("- test: python -m pytest -q", call.Prompt);
        Assert.Contains("Merge of main into pull request #7", Assert.Single(world.Calls("reviewer")).Prompt);
        Assert.Single(world.MergePublishes);
        var reply = Reply(world);
        Assert.Contains("| `src/app.py` (both changed it) | combined: Kept both changes in src/app.py. |", reply);
        Assert.Contains("Resolved the merge.", reply);
        Assert.Contains(world.Comments, c => c.Contains("left 2 files in conflict"));
    }

    [SkippableFact]
    public async Task A_conflict_marker_left_behind_goes_back_to_the_resolver_before_any_check_runs()
    {
        var world = Conflicted("src/app.py")
            .Agent("merge-resolver", Say(Answers.Merge("done", "src/app.py")), Say(Answers.Merge("done", "src/app.py")))
            .Markers(["src/app.py"]);

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("update_pushed", result.Status);
        Assert.Equal(2, world.Calls("merge-resolver").Count());
        Assert.Contains("Conflict markers", world.Calls("merge-resolver").ElementAt(1).Prompt);
        Assert.Contains("src/app.py", world.Calls("merge-resolver").ElementAt(1).Prompt);
        Assert.Single(world.CheckCalls); // only the second round got as far as the checks
    }

    [SkippableFact]
    public async Task When_the_merged_code_fails_the_checks_the_resolver_is_asked_to_fix_it()
    {
        var world = new FakeWorld()
            .Agent("merge-resolver", Say(Answers.Merge("done")))
            .Agent("reviewer", Say(Answers.Review()))
            .Changes(NothingChanged)
            .Check("python -m pytest -q", FakeWorld.Fails("NameError: greet is not defined"), FakeWorld.Passes());

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("update_pushed", result.Status);
        var call = Assert.Single(world.Calls("merge-resolver"));
        Assert.Contains("Git combined every file by itself", call.Prompt);
        Assert.Contains("NameError: greet is not defined", call.Prompt);
        Assert.Equal(["merge-resolver", "reviewer"], Roles(world)); // what an agent wrote is reviewed
        Assert.Single(world.MergePublishes);
    }

    [SkippableFact]
    public async Task A_conflict_in_a_protected_file_is_left_to_a_person_without_asking_an_agent()
    {
        var world = new FakeWorld { Merge = Conflicting(".github/workflows/ci.yml", "src/app.py") };

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("update_blocked", result.Status);
        Assert.Empty(world.AgentCalls);
        Assert.Empty(world.MergePublishes);
        Assert.Contains(world.Comments, c => c.Contains("protected files in conflict") && c.Contains(".github/workflows/ci.yml") && !c.Contains("`src/app.py`"));
    }

    [SkippableFact]
    public async Task Too_many_conflicting_files_are_left_to_a_person()
    {
        var world = new FakeWorld { Merge = Conflicting(Enumerable.Range(1, GitWorkspace.MaxConflictedFiles + 1).Select(i => $"src/f{i}.py").ToArray()) };

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("update_blocked", result.Status);
        Assert.Empty(world.AgentCalls);
        Assert.Contains(world.Comments, c => c.Contains("too many conflicts"));
    }

    [SkippableFact]
    public async Task A_resolver_that_cannot_continue_pushes_nothing()
    {
        var world = new FakeWorld { Merge = Conflicting("src/app.py") }.Agent("merge-resolver", Say(Answers.Merge("blocked")));

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("update_blocked", result.Status);
        Assert.Empty(world.MergePublishes);
        Assert.Contains(world.Comments, c => c.Contains("**Update: I cannot continue**") && c.Contains("A person has to merge this one"));
    }

    [SkippableFact]
    public async Task A_resolver_answer_that_skips_a_conflicted_file_is_sent_back_once()
    {
        var world = new FakeWorld { Merge = Conflicting("a.py", "b.py") }
            .Agent("merge-resolver", Say(Answers.Merge("done", "a.py")), Say(Answers.Merge("done", "a.py", "b.py")))
            .Agent("reviewer", Say(Answers.Review()));

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("update_pushed", result.Status);
        Assert.Contains("b.py has no resolution", world.Calls("merge-resolver").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task A_merge_git_cannot_make_is_reported_and_nothing_is_pushed()
    {
        var world = new FakeWorld { MergeFailure = new Temporalio.Exceptions.ApplicationFailureException("refusing to merge unrelated histories", nonRetryable: true) };

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("update_blocked", result.Status);
        Assert.Empty(world.MergePublishes);
        Assert.Contains(world.Comments, c => c.Contains("**Update: I could not merge**") && c.Contains("unrelated histories"));
    }

    [SkippableFact]
    public async Task Rounds_that_keep_failing_push_nothing()
    {
        var world = Conflicted("src/app.py")
            .Agent("merge-resolver", Enumerable.Repeat(Say(Answers.Merge("done", "src/app.py")), 3).ToArray())
            .Check("python -m pytest -q", Enumerable.Repeat(FakeWorld.Fails("still red"), 3).ToArray());

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("build_failed", result.Status);
        Assert.Empty(world.MergePublishes);
        Assert.Contains("still red", world.Calls("merge-resolver").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task A_push_that_fails_is_reported_with_how_to_ask_again()
    {
        var world = Conflicted("src/app.py");
        // Replace the fake push with one that fails, as when a person pushed to the branch meanwhile.
        world.PublishMergeFailure = new Temporalio.Exceptions.ApplicationFailureException("The branch factory/issue-7 changed while the update was being made, so nothing was pushed.", nonRetryable: true);

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("publish_failed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("pushing it failed") && c.Contains("Comment `/update` again"));
    }

    [SkippableFact]
    public async Task On_a_ticket_without_an_open_pull_request_the_person_is_told()
    {
        var world = new FakeWorld { UpdateContext = new UpdateContext(null, "factory-bot") };

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update(onPullRequest: false)));

        Assert.Equal("update_no_pull_request", result.Status);
        Assert.Contains(TicketComments.UpdateNoPullRequest, world.Comments);
        Assert.Empty(world.PrepareRequests);
    }

    [SkippableFact]
    public async Task Somebody_elses_pull_request_is_ignored_without_a_word()
    {
        var world = new FakeWorld { UpdateContext = new UpdateContext(FakeWorld.FactoryPullRequest(author: "alice").Pull, "factory-bot") };

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("ignored", result.Status);
        Assert.Empty(world.Comments);
        Assert.Empty(world.PrepareRequests);
    }

    [SkippableFact]
    public async Task A_merged_pull_request_is_not_updated()
    {
        var world = new FakeWorld { UpdateContext = new UpdateContext(FakeWorld.FactoryPullRequest(merged: true, state: "closed").Pull, "factory-bot") };

        var result = await world.RunAsync(fixture, context => context.RunUpdateAsync(ScenarioContext.Update()));

        Assert.Equal("ignored", result.Status);
        Assert.Contains(TicketComments.UpdateClosed, world.Comments);
        Assert.Empty(world.PrepareRequests);
    }
}
