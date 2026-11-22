using Temporalio.Client;

namespace AiSdlcFactory.Controller.Tests.Scenarios;

/// <summary>What happens to human comments that no stage can use: the person is told, and the run is not disturbed.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class CommentScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static FakeWorld ToPullRequest() => new FakeWorld()
        .Agent("analyst", Say(Answers.Analyst()))
        .Agent("coverage", Say(Answers.Coverage("missing", "missing")))
        .Agent("developer", Say(Answers.Developer()))
        .Agent("reviewer", Say(Answers.Review()))
        .Agent("acceptance", Say(Answers.Qa("met", "met")));

    private static HumanReply Comment(string text) => new("bob", text);

    private static int Count(FakeWorld world, string text) => world.Snapshot(world.Comments).Count(c => c.Contains(text));

    [SkippableFact]
    public async Task A_comment_during_the_build_is_answered_once_and_does_not_change_the_run()
    {
        var world = ToPullRequest();
        WorkflowHandle<FeatureWorkflow, WorkResult>? running = null;
        var sent = false;
        world.BeforeAgent = role =>
        {
            if (role == "developer" && !sent)
            {
                sent = true;
                running!.SignalAsync(w => w.OnHumanReplyAsync(Comment("Please also add a French greeting."))).GetAwaiter().GetResult();
                running.SignalAsync(w => w.OnHumanReplyAsync(Comment("And make it blue."))).GetAwaiter().GetResult();
            }
        };

        var result = await world.RunAsync(fixture, async context =>
        {
            running = await context.StartAsync(ScenarioContext.Request());
            return await ScenarioContext.ResultAsync(running);
        });

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(1, Count(world, "it cannot take new input")); // two comments, one notice
        Assert.Contains("close and reopen the ticket", world.Snapshot(world.Comments).First(c => c.Contains("it cannot take new input")));
        Assert.DoesNotContain("French", world.Calls("developer").Single().Prompt); // the comment was not smuggled into the task
        Assert.Equal(0, Count(world, "no stage was waiting")); // nothing was left over to report at the end
        Assert.Empty(world.Problems);
    }

    [SkippableFact]
    public async Task A_comment_that_no_stage_ever_read_is_reported_when_the_run_ends()
    {
        var world = ToPullRequest();
        WorkflowHandle<FeatureWorkflow, WorkResult>? running = null;
        var sent = false;
        // Sent while the analyst is working; the analyst passes, no design review follows, so nobody reads it.
        world.BeforeAgent = role =>
        {
            if (role == "analyst" && !sent)
            {
                sent = true;
                running!.SignalAsync(w => w.OnHumanReplyAsync(Comment("By the way, use the text Goodbye."))).GetAwaiter().GetResult();
            }
        };

        var result = await world.RunAsync(fixture, async context =>
        {
            running = await context.StartAsync(ScenarioContext.Request());
            return await ScenarioContext.ResultAsync(running);
        });

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(1, Count(world, "1 comment arrived during this run, but no stage was waiting for an answer, so it was not used"));
        Assert.Equal(0, Count(world, "it cannot take new input"));
    }

    [SkippableFact]
    public async Task A_comment_the_analyst_asked_for_is_used_and_nothing_is_reported()
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
            await handle.SignalAsync(w => w.OnHumanReplyAsync(Comment("Use the text Goodbye, world!")));
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("pr_opened", result.Status);
        Assert.Equal(0, Count(world, "was not used"));
        Assert.Equal(0, Count(world, "no stage was waiting"));
        Assert.Equal(0, Count(world, "it cannot take new input"));
    }

    [SkippableFact]
    public async Task A_ticket_a_person_closed_gets_no_notice_about_unread_comments()
    {
        var world = new FakeWorld().Agent("analyst", Say(Answers.Analyst("needs_clarification")));

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartAsync(ScenarioContext.Request());
            await world.WaitForCommentAsync(c => c.Contains("I need answers before building"));
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("closed", result.Status);
        Assert.Equal(0, Count(world, "was not used"));
        Assert.Equal(0, Count(world, "no stage was waiting"));
    }
}
