using System.Text.Json;

namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>The routing workflow driving feature runs: restarts, duplicates and comments reaching a waiting run.</summary>
[Collection("temporal")]
public sealed class RouterScenarios(TemporalFixture fixture)
{
    private static ForgejoEvent Event(string name, string action, string repo, string? comment = null, string state = "open")
    {
        var payload = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["repository"] = new { full_name = $"acme/{repo}" },
            ["issue"] = new
            {
                number = 1, title = "Add goodbye", body = "Add a goodbye greeting.", state,
                user = new { login = "alice" }, labels = new[] { new { name = "feature" } }, pull_request = (object?)null,
            },
        };
        if (comment is not null)
        {
            payload["comment"] = new { body = comment, user = new { login = "bob" } };
        }
        return new ForgejoEvent(Guid.NewGuid().ToString(), name, JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement);
    }

    private static Task<ForgejoReceipt> Route(ScenarioContext context, ForgejoEvent input) =>
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
    public async Task A_second_opening_event_is_ignored_but_reopening_runs_the_factory_again()
    {
        var world = new FakeWorld().Agent("analyst",
            AgentReply.Says(Answers.Analyst("reject")), AgentReply.Says(Answers.Analyst("reject")));
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issues", "opened", repo));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 1);

            await Route(context, Event("issues", "label_updated", repo));
            await Task.Delay(1500);
            Assert.Single(world.Calls("analyst")); // a label change on a ticket that already ran does nothing

            await Route(context, Event("issues", "reopened", repo));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 2);
            return 0;
        });

        Assert.Equal(2, world.Calls("analyst").Count());
    }

    [SkippableFact]
    public async Task A_comment_on_the_ticket_reaches_the_waiting_run()
    {
        var world = new FakeWorld().Agent("analyst",
            AgentReply.Says(Answers.Analyst("needs_clarification")), AgentReply.Says(Answers.Analyst("reject")));
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issues", "opened", repo));
            await world.WaitForCommentAsync(c => c.Contains("I need answers before building"));

            await Route(context, Event("issue_comment", "created", repo, comment: "Use the text Goodbye, world!"));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 1);
            return 0;
        });

        Assert.Contains("Use the text Goodbye, world!", world.Calls("analyst").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task The_factorys_own_comments_and_closed_tickets_do_not_wake_anything()
    {
        var world = new FakeWorld().Agent("analyst", AgentReply.Says(Answers.Analyst("needs_clarification")));
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issues", "opened", repo));
            await world.WaitForCommentAsync(c => c.Contains("I need answers before building"));

            await Route(context, Event("issue_comment", "created", repo, comment: $"{EventRouter.BotMarker}\nThe factory talking to itself"));
            await Task.Delay(1500);
            Assert.Single(world.Calls("analyst"));

            await Route(context, Event("issues", "closed", repo, state: "closed"));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 1);
            return 0;
        });

        Assert.Single(world.Calls("analyst"));
    }
}
