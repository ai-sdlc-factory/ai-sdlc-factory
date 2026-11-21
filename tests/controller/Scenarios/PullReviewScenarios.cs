namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>/review: the reviewers read a pull request's diff and post their findings on it. Whoever opened it, nothing is written, pushed or merged.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class PullReviewScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static FakeWorld Reviewing(params string[] answers) =>
        new FakeWorld().Agent("reviewer", (answers.Length == 0 ? [Answers.Review()] : answers).Select(Say).ToArray());

    private static string[] Roles(FakeWorld world) => world.Snapshot(world.AgentCalls).Select(call => call.Role).ToArray();

    [SkippableFact]
    public async Task A_persons_pull_request_is_reviewed_and_the_findings_are_posted_on_it_and_nothing_else_happens()
    {
        var world = Reviewing(Answers.Review(("major", "goodbye() ignores its argument.")));
        var request = ScenarioContext.PullReview();

        var result = await world.RunAsync(fixture, context => context.RunPullReviewAsync(request));

        Assert.Equal("review_posted", result.Status);
        Assert.Equal(["reviewer"], Roles(world));
        Assert.Empty(world.Problems);
        Assert.Contains(world.Comments, c => c.StartsWith("I am reviewing pull request #7. I only read it: nothing will be changed."));
        Assert.Contains(world.Comments, c => c.Contains("**Review (reviewer): changes needed**") && c.Contains("goodbye() ignores its argument."));
        var done = Assert.Single(world.Comments, c => c.StartsWith("**Review of pull request #7 finished**"));
        Assert.Contains("- reviewer: 1 blocking finding", done);
        Assert.Contains("nothing was changed, and I do not merge", done);
        // Read only: nothing was written, published, opened or saved.
        Assert.Empty(world.Publishes);
        Assert.Empty(world.PullRequests);
        Assert.Empty(world.Rescues);
        Assert.Empty(world.MergePublishes);
        Assert.Equal([request.WorkflowId], world.CleanedUp);
    }

    [SkippableFact]
    public async Task The_checkout_is_a_new_local_branch_off_the_default_branch_so_the_projects_settings_never_come_from_the_pull_requests_branch()
    {
        var world = Reviewing();

        await world.RunAsync(fixture, context => context.RunPullReviewAsync(ScenarioContext.PullReview()));

        var prepare = Assert.Single(world.PrepareRequests);
        Assert.False(prepare.ExistingBranch);
        Assert.Equal("factory/review-7", prepare.Branch);
    }

    [SkippableFact]
    public async Task The_reviewer_is_given_the_diff_and_told_the_checks_were_not_run()
    {
        var world = Reviewing();

        await world.RunAsync(fixture, context => context.RunPullReviewAsync(ScenarioContext.PullReview()));

        var call = Assert.Single(world.Calls("reviewer"));
        Assert.False(call.Writable);
        Assert.Equal(SandboxNetworks.Model, call.Network);
        Assert.Contains("+def goodbye():", call.Prompt);
        Assert.Contains("- modified: hello.py", call.Prompt);
        Assert.Contains("- added: test_hello.py", call.Prompt);
        Assert.Contains("did not run the project's checks on this pull request", call.Prompt);
        Assert.DoesNotContain("Automated checks already passed", call.Prompt);
        Assert.Contains("Ticket #7", call.Prompt);
        Assert.Contains("Add goodbye", call.Prompt); // the pull request's own title stands in for the ticket
    }

    [SkippableFact]
    public async Task Every_reviewer_the_project_lists_reads_the_pull_request()
    {
        var world = Reviewing().Agent("security", Say(Answers.Review(("minor", "Consider logging."))));
        world.ProjectConfig = """{"image":"python","reviewers":["reviewer","security"],"gates":[{"name":"test","command":["python","-m","pytest","-q"]}]}""";

        var result = await world.RunAsync(fixture, context => context.RunPullReviewAsync(ScenarioContext.PullReview()));

        Assert.Equal("review_posted", result.Status);
        Assert.Equal(["reviewer", "security"], Roles(world));
        var done = Assert.Single(world.Comments, c => c.StartsWith("**Review of pull request #7 finished**"));
        Assert.Contains("- reviewer: no blocking findings", done);
        Assert.Contains("- security: no blocking findings, 1 other note", done);
        Assert.Empty(world.CheckCalls); // the project's checks are not run
    }

    [SkippableFact]
    public async Task A_pull_request_from_a_fork_is_reviewed_the_same_way()
    {
        var world = Reviewing();
        world.ReviewContext = world.ReviewContext with { Pull = FakeWorld.FactoryPullRequest(author: "mallory", headRef: "main", headRepo: "mallory/shop").Pull };

        var result = await world.RunAsync(fixture, context => context.RunPullReviewAsync(ScenarioContext.PullReview()));

        Assert.Equal("review_posted", result.Status);
        Assert.Equal("factory/review-7", Assert.Single(world.PrepareRequests).Branch); // nothing of the fork is fetched
    }

    [SkippableFact]
    public async Task A_long_diff_is_said_to_be_cut_for_the_reviewers_and_in_the_summary()
    {
        var world = Reviewing();
        world.ReviewContext = world.ReviewContext with { Changes = new PullChanges([new ChangedFile("big.py", "added", 5000, 0)], 1, new string('+', 50_000), true) };

        await world.RunAsync(fixture, context => context.RunPullReviewAsync(ScenarioContext.PullReview()));

        Assert.Contains("The diff is cut; only its start is shown.", Assert.Single(world.Calls("reviewer")).Prompt);
        Assert.Contains("The diff was too long to read in full", Assert.Single(world.Comments, c => c.StartsWith("**Review of pull request #7 finished**")));
    }

    [SkippableFact]
    public async Task A_pull_request_that_is_closed_is_not_reviewed()
    {
        var world = Reviewing();
        world.ReviewContext = world.ReviewContext with { Pull = FakeWorld.FactoryPullRequest(state: "closed", merged: true).Pull };

        var result = await world.RunAsync(fixture, context => context.RunPullReviewAsync(ScenarioContext.PullReview()));

        Assert.Equal("ignored", result.Status);
        Assert.Contains(TicketComments.PullReviewClosed, world.Comments);
        Assert.Empty(world.PrepareRequests);
        Assert.Empty(world.AgentCalls);
    }

    [SkippableFact]
    public async Task When_the_host_cannot_say_what_changed_nothing_is_reviewed_and_the_person_is_told()
    {
        var world = Reviewing();
        world.ReviewContext = world.ReviewContext with { Changes = null };

        var result = await world.RunAsync(fixture, context => context.RunPullReviewAsync(ScenarioContext.PullReview()));

        Assert.Equal("error", result.Status);
        Assert.Contains(TicketComments.PullReviewNoChanges, world.Comments);
        Assert.Empty(world.AgentCalls);
    }

    [SkippableFact]
    public async Task A_reviewer_that_is_not_signed_in_stops_the_review_without_a_summary()
    {
        var world = new FakeWorld().Agent("reviewer", AgentReply.NotSignedIn);

        var result = await world.RunAsync(fixture, context => context.RunPullReviewAsync(ScenarioContext.PullReview()));

        Assert.Equal("blocked", result.Status);
        Assert.DoesNotContain(world.Comments, c => c.StartsWith("**Review of pull request #7 finished**"));
    }

    [SkippableFact]
    public async Task The_run_is_added_to_the_usage_log_under_the_pull_requests_number()
    {
        var world = Reviewing();

        await world.RunAsync(fixture, context => context.RunPullReviewAsync(ScenarioContext.PullReview()));

        var entry = Assert.Single(world.Snapshot(world.UsageLogs));
        Assert.Equal(("review", 7, "review_posted"), (entry.Kind, entry.Number, entry.Status));
    }
}
