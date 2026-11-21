using Temporalio.Client;

namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>The plan workflow: a planner proposes tickets, and nothing is created until a person approves.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class PlanScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static WorkRequest PlanTicket() =>
        ScenarioContext.Request(title: "Add a shopping list", body: "Users want a shopping list with a screen and an API.", labels: ["plan"]) with { Kind = WorkIds.Plan };

    private static FakeWorld Planning(params string[] answers) => new FakeWorld().Agent("planner", answers.Length == 0 ? [Say(Answers.Plan())] : answers.Select(Say).ToArray());

    private static int Count(FakeWorld world, string text) => world.Snapshot(world.Comments).Count(c => c.Contains(text));

    private static async Task WaitForCountAsync(FakeWorld world, string text, int count)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (Count(world, text) < count)
        {
            Assert.True(DateTime.UtcNow < until, $"'{text}' did not reach {count} comments. Comments:\n" + string.Join("\n---\n", world.Snapshot(world.Comments)));
            await Task.Delay(50);
        }
    }

    private static Task Say_(WorkflowHandle<PlanWorkflow, WorkResult> handle, string text, string who = "bob") =>
        handle.SignalAsync(w => w.OnHumanReplyAsync(new HumanReply(who, text)));

    [SkippableFact]
    public async Task A_plan_is_proposed_and_nothing_is_created_until_a_person_approves()
    {
        var world = Planning();

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartPlanAsync(PlanTicket());
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("closed", result.Status);
        Assert.Empty(world.CreatedIssues);
        Assert.Empty(world.PullRequests);
        Assert.Empty(world.Publishes);
        Assert.Empty(world.Problems);
        Assert.Contains(world.Comments, c => c.Contains("3. Part 3") && c.Contains("Waits for: 1"));
        var call = world.Calls("planner").Single();
        Assert.False(call.Writable);
        Assert.Equal(SandboxNetworks.Model, call.Network);
        Assert.Contains("Users want a shopping list", call.Prompt);
    }

    [SkippableFact]
    public async Task An_approval_that_arrived_while_the_planner_was_working_is_dropped_with_a_notice()
    {
        var world = Planning();
        var started = new TaskCompletionSource<WorkflowHandle<PlanWorkflow, WorkResult>>();
        // The person approves while the planner is still working, so before any plan has been posted.
        world.BeforeAgent = _ => started.Task.Result.SignalAsync(w => w.OnHumanReplyAsync(new HumanReply("bob", "/approve"))).GetAwaiter().GetResult();

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartPlanAsync(PlanTicket());
            started.SetResult(handle);
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 1);
            await WaitForCountAsync(world, PlanComments.ApprovalWhilePlanning, 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("closed", result.Status);
        Assert.Empty(world.CreatedIssues);
    }

    [SkippableFact]
    public async Task A_plain_approval_creates_only_the_tickets_that_wait_for_nothing_and_the_rest_when_named()
    {
        var world = Planning();

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartPlanAsync(PlanTicket());
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 1);
            await Say_(handle, "/approve");
            await WaitForCountAsync(world, "Created 2 tickets from this plan", 1);
            Assert.Equal(2, world.CreatedIssues.Count);
            await Say_(handle, "/approve");
            await WaitForCountAsync(world, "Every ticket that waits for nothing was already created", 1);
            await Say_(handle, "/approve 3", "carol");
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("plan_created", result.Status);
        Assert.Equal(["Part 1", "Part 2", "Part 3"], world.CreatedIssues.Select(i => i.Title));
        Assert.All(world.CreatedIssues, issue => Assert.Equal(["feature"], issue.Labels));
        Assert.Contains("from plan #1, at the request of bob. Item 1 of 3.", world.CreatedIssues[0].Body);
        Assert.Contains("- Criterion A of part 1", world.CreatedIssues[0].Body);
        // The third ticket names the ticket that was opened for the first one, because by then it exists.
        Assert.Contains("at the request of carol", world.CreatedIssues[2].Body);
        Assert.Contains("- #91: Part 1", world.CreatedIssues[2].Body);
        Assert.Contains(world.Comments, c => c.Contains("Not created yet: 3. Reply `/approve 3`"));
        Assert.Contains(world.Comments, c => c.Contains("Created 1 ticket from this plan") && c.Contains("http://git.test/issues/93"));
    }

    [SkippableFact]
    public async Task Naming_tickets_creates_exactly_those_and_unknown_or_repeated_numbers_are_answered()
    {
        var world = Planning();

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartPlanAsync(PlanTicket());
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 1);
            await Say_(handle, "/approve 2 9");
            await WaitForCountAsync(world, "Created 1 ticket from this plan", 1);
            await Say_(handle, "/approve 2");
            await WaitForCountAsync(world, "Already created", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("closed", result.Status);
        Assert.Equal(["Part 2"], world.CreatedIssues.Select(i => i.Title));
        Assert.Contains(world.Comments, c => c.Contains("There is no ticket 9 in this plan. The tickets are 1 to 3."));
    }

    [SkippableFact]
    public async Task A_comment_that_asks_for_a_change_makes_the_planner_plan_again_with_the_old_plan_and_the_comment()
    {
        var world = Planning(Answers.Plan(), Answers.Plan(tickets: 2));

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartPlanAsync(PlanTicket());
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 1);
            await Say_(handle, "Merge the API and the screen into one ticket.");
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 2);
            await Say_(handle, "/approve");
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("plan_created", result.Status);
        Assert.Equal(["Part 1", "Part 2"], world.CreatedIssues.Select(i => i.Title));
        var second = world.Calls("planner").ElementAt(1).Prompt;
        Assert.Contains("Merge the API and the screen into one ticket.", second);
        Assert.Contains("3. Part 3 (depends on 1)", second);
    }

    [SkippableFact]
    public async Task A_comment_that_tries_to_approve_and_gets_it_wrong_is_explained_and_does_not_make_the_planner_plan_again()
    {
        var world = Planning();

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartPlanAsync(PlanTicket());
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 1);
            await Say_(handle, "/approve please");
            await WaitForCountAsync(world, "I did not understand that as an approval", 1);
            await Say_(handle, "/approve");
            await WaitForCountAsync(world, "Created 2 tickets from this plan", 1);
            await Say_(handle, "/approve 3");
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("plan_created", result.Status);
        Assert.Single(world.Calls("planner")); // no second plan was made for "/approve please"
        Assert.Equal(["Part 1", "Part 2", "Part 3"], world.CreatedIssues.Select(i => i.Title));
        Assert.Equal(1, Count(world, "I did not understand that as an approval"));
    }

    [SkippableFact]
    public async Task Once_a_ticket_exists_the_plan_is_final_and_a_change_gets_one_notice()
    {
        var world = Planning();

        await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartPlanAsync(PlanTicket());
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 1);
            await Say_(handle, "/approve 1");
            await WaitForCountAsync(world, "Created 1 ticket from this plan", 1);
            await Say_(handle, "Actually, drop the screen.");
            await Say_(handle, "And rename part 2.");
            await WaitForCountAsync(world, "I will not change it", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Single(world.Calls("planner"));
        Assert.Equal(1, Count(world, "I will not change it"));
        Assert.Single(world.CreatedIssues);
    }

    [SkippableFact]
    public async Task An_approval_in_the_same_breath_as_a_change_creates_nothing()
    {
        var world = Planning(Answers.Plan(), Answers.Plan(tickets: 1));

        await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartPlanAsync(PlanTicket());
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 1);
            await Say_(handle, "Only one ticket please.");
            await Say_(handle, "/approve");
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 2);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Empty(world.CreatedIssues);
    }

    [SkippableFact]
    public async Task An_approval_that_arrives_while_the_planner_is_working_is_not_applied_to_the_new_plan()
    {
        var world = Planning(Answers.Plan(), Answers.Plan(tickets: 2));
        // The person approves while the second plan is still being written.
        WorkflowHandle<PlanWorkflow, WorkResult>? running = null;
        var plannerRuns = 0;
        world.BeforeAgent = role =>
        {
            if (role == "planner" && Interlocked.Increment(ref plannerRuns) == 2)
            {
                running!.SignalAsync(w => w.OnHumanReplyAsync(new HumanReply("bob", "/approve"))).GetAwaiter().GetResult();
            }
        };

        await world.RunAsync(fixture, async context =>
        {
            var handle = running = await context.StartPlanAsync(PlanTicket());
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 1);
            await Say_(handle, "Only two tickets please.");
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 2);
            await WaitForCountAsync(world, "An approval arrived while I was still planning", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Empty(world.CreatedIssues);
    }

    [SkippableFact]
    public async Task A_ticket_that_cannot_be_opened_is_reported_and_the_rest_can_be_tried_again()
    {
        var world = Planning();
        world.IssueFailure = new Temporalio.Exceptions.ApplicationFailureException("The repository has no \"feature\" label, so the ticket could not be opened.", nonRetryable: true);

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartPlanAsync(PlanTicket());
            await WaitForCountAsync(world, "**Plan: proposed tickets**", 1);
            await Say_(handle, "/approve");
            await WaitForCountAsync(world, "I could not open the ticket", 1);
            world.IssueFailure = null;
            await Say_(handle, "/approve");
            await WaitForCountAsync(world, "Created 2 tickets from this plan", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("closed", result.Status);
        Assert.Contains(world.Comments, c => c.Contains("has no \"feature\" label"));
        Assert.Equal(2, world.CreatedIssues.Count);
    }

    [SkippableFact]
    public async Task A_planner_that_rejects_stops_the_run_and_one_that_asks_gets_the_answer()
    {
        var rejected = new FakeWorld().Agent("planner", Say(Answers.Plan("reject")));
        var rejectedResult = await rejected.RunAsync(fixture, context => context.RunToEndAsync(PlanTicket()));
        Assert.Equal("rejected", rejectedResult.Status);

        var asking = Planning(Answers.Plan("needs_clarification"), Answers.Plan());
        var result = await asking.RunAsync(fixture, async context =>
        {
            var handle = await context.StartPlanAsync(PlanTicket());
            await WaitForCountAsync(asking, "Which database?", 1);
            await Say_(handle, "SQLite.");
            await WaitForCountAsync(asking, "**Plan: proposed tickets**", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });
        Assert.Equal("closed", result.Status);
        Assert.Contains("SQLite.", asking.Calls("planner").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task An_unusable_answer_twice_ends_the_run_as_plan_failed()
    {
        var world = Planning("not json", "still not json");

        var result = await world.RunAsync(fixture, async context =>
            await ScenarioContext.ResultAsync(await context.StartPlanAsync(PlanTicket())));

        Assert.Equal("plan_failed", result.Status);
        Assert.Empty(world.CreatedIssues);
    }
}
