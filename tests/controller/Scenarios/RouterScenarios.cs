using System.Text.Json;

namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>The routing workflow driving feature runs: restarts, duplicates and comments reaching a waiting run.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class RouterScenarios(TemporalFixture fixture)
{
    private static WebhookDelivery Event(string name, string action, string repo, string? comment = null, string state = "open", string label = "feature")
    {
        var payload = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["repository"] = new { full_name = $"acme/{repo}" },
            ["issue"] = new
            {
                number = 1, title = "Add goodbye", body = "Add a goodbye greeting.", state,
                user = new { login = "alice" }, labels = new[] { new { name = label } }, pull_request = (object?)null,
            },
        };
        if (comment is not null)
        {
            payload["comment"] = new { body = comment, user = new { login = "bob" } };
        }
        return new WebhookDelivery(Guid.NewGuid().ToString(), name, JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement);
    }

    private static Task<EventReceipt> Route(ScenarioContext context, WebhookDelivery input) =>
        context.Client.StartWorkflowAsync(
            (RepoEventWorkflow workflow) => workflow.RunAsync(input),
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
    public async Task A_comment_on_a_ticket_with_no_running_workflow_gets_a_notice_instead_of_silence()
    {
        var world = new FakeWorld();
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issue_comment", "created", repo, comment: "Why did you stop?"));
            await Route(context, Event("issue_comment", "created", repo, comment: "Hello? Anyone?"));
            return 0;
        });

        // Two comments, one notice: the notice is not repeated while it is still among the latest comments.
        var notice = Assert.Single(world.Snapshot(world.Notices));
        Assert.Equal(TicketComments.NoRunActive, notice);
        Assert.Contains("close and reopen the ticket", notice);
        Assert.Empty(world.Calls("analyst"));
    }

    [SkippableFact]
    public async Task An_incident_ticket_runs_the_incident_workflow_and_a_comment_reaches_it()
    {
        var world = new FakeWorld()
            .Agent("incident-analyst", AgentReply.Says(Answers.IncidentAssessment("needs_clarification")), AgentReply.Says(Answers.IncidentAssessment("reject")));
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issues", "opened", repo, label: "incident"));
            await world.WaitForCommentAsync(c => c.Contains("I need more information"));
            await Route(context, Event("issue_comment", "created", repo, comment: "Production, EU.", label: "incident"));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 1);
            return 0;
        });

        Assert.Equal(2, world.Calls("incident-analyst").Count()); // the comment reached the waiting incident run
        Assert.Contains("Production, EU.", world.Calls("incident-analyst").ElementAt(1).Prompt);
        Assert.Empty(world.Calls("analyst"));
        Assert.Empty(world.Snapshot(world.Notices)); // it was delivered, so no "nobody is listening" notice
    }

    [SkippableFact]
    public async Task A_label_the_hosts_triggers_name_starts_the_workflow_and_the_factory_command_starts_one_when_the_host_allows_it()
    {
        var world = new FakeWorld().Agent("analyst", AgentReply.Says(Answers.Analyst("reject")), AgentReply.Says(Answers.Analyst("reject")));
        world.Triggers = new TriggerRules(new Dictionary<string, IReadOnlyList<string>> { ["feature"] = ["enhancement"] }, Command: true);
        var byLabel = "r" + Guid.NewGuid().ToString("N")[..10];
        var byCommand = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issues", "opened", byLabel, label: "enhancement"));
            await Route(context, Event("issue_comment", "created", byCommand, comment: "/factory feature", label: "question"));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 2);
            return 0;
        });

        Assert.Equal(2, world.Calls("analyst").Count());
        Assert.All(world.Snapshot(world.PrepareRequests), request => Assert.StartsWith("factory/issue-", request.Branch));
    }

    [SkippableFact]
    public async Task Without_the_hosts_permission_the_factory_command_and_an_unknown_label_start_nothing()
    {
        var world = new FakeWorld();
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issue_comment", "created", repo, comment: "/factory feature", label: "question"));
            await Route(context, Event("issues", "opened", repo, label: "enhancement"));
            return 0;
        });

        Assert.Empty(world.Calls("analyst"));
        Assert.Empty(world.Snapshot(world.PrepareRequests));
    }

    [SkippableFact]
    public async Task A_comment_the_factory_wrote_itself_gets_no_notice()
    {
        var world = new FakeWorld();
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issue_comment", "created", repo, comment: EventRouter.BotMarker + "\nSomething the factory said"));
            return 0;
        });

        Assert.Empty(world.Snapshot(world.Notices));
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
    public async Task Retry_runs_the_factory_again_on_a_ticket_whose_run_has_finished()
    {
        var world = new FakeWorld().Agent("analyst",
            AgentReply.Says(Answers.Analyst("reject")), AgentReply.Says(Answers.Analyst("reject")));
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issues", "opened", repo));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 1);

            await Route(context, Event("issue_comment", "created", repo, comment: "/retry"));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 2);
            return 0;
        });

        Assert.Equal(2, world.Calls("analyst").Count());
        Assert.Empty(world.Snapshot(world.Notices));
    }

    [SkippableFact]
    public async Task Retry_while_a_run_is_still_active_changes_nothing_and_says_so()
    {
        var world = new FakeWorld().Agent("analyst",
            AgentReply.Says(Answers.Analyst("needs_clarification")), AgentReply.Says(Answers.Analyst("reject")));
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issues", "opened", repo));
            await world.WaitForCommentAsync(c => c.Contains("I need answers before building"));

            await Route(context, Event("issue_comment", "created", repo, comment: "/retry"));
            await Route(context, Event("issue_comment", "created", repo, comment: "Use the text Goodbye, world!"));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 1);
            return 0;
        });

        Assert.Equal(2, world.Calls("analyst").Count()); // one run: the question, then the answer; /retry was not taken as an answer
        Assert.DoesNotContain("/retry", world.Calls("analyst").Last().Prompt);
        Assert.Equal(TicketComments.RetryWhileRunning, Assert.Single(world.Snapshot(world.Notices)));
    }

    [SkippableFact]
    public async Task Retry_on_a_ticket_that_already_has_an_open_pull_request_says_so_and_starts_nothing()
    {
        var world = new FakeWorld().Agent("analyst", AgentReply.Says(Answers.Analyst("reject")), AgentReply.Says(Answers.Analyst("reject")));
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issues", "opened", repo));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 1);

            world.OpenFactoryPull = 12;
            await Route(context, Event("issue_comment", "created", repo, comment: "/retry"));
            return 0;
        });

        Assert.Single(world.Calls("analyst"));
        Assert.Equal(TicketComments.RetryHasPullRequest(12), Assert.Single(world.Snapshot(world.Notices)));
    }

    [SkippableFact]
    public async Task Retry_restart_starts_a_new_run_even_when_the_ticket_has_an_open_pull_request()
    {
        var world = new FakeWorld().Agent("analyst", AgentReply.Says(Answers.Analyst("reject")), AgentReply.Says(Answers.Analyst("reject")));
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issues", "opened", repo));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 1);

            world.OpenFactoryPull = 12;
            await Route(context, Event("issue_comment", "created", repo, comment: "/retry restart"));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 2);
            return 0;
        });

        Assert.Equal(2, world.Calls("analyst").Count());
        Assert.Empty(world.Snapshot(world.Notices));
    }

    [SkippableFact]
    public async Task Reopening_a_ticket_right_after_closing_it_starts_a_new_run_once_the_old_one_has_stopped()
    {
        var world = new FakeWorld().Agent("analyst",
            AgentReply.Says(Answers.Analyst("needs_clarification")), AgentReply.Says(Answers.Analyst("reject")));
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issues", "opened", repo));
            await world.WaitForCommentAsync(c => c.Contains("I need answers before building"));

            // The first run is still waiting for an answer when the ticket is closed and at once reopened.
            await Route(context, Event("issues", "closed", repo, state: "closed"));
            await Route(context, Event("issues", "reopened", repo));
            await WaitUntilAsync(() => world.Snapshot(world.CleanedUp).Count == 2);
            return 0;
        });

        Assert.Equal(2, world.Calls("analyst").Count());
        Assert.Empty(world.Snapshot(world.Notices));
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

    // ---- who may steer the factory

    [SkippableFact]
    public async Task A_refused_command_starts_nothing_and_gets_one_notice_that_names_the_rule_not_the_person()
    {
        var verdict = new CommandVerdict(false, CommandRefusal.TooLittleAccess, "write", false);
        var world = new FakeWorld { CommandVerdict = verdict };
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issue_comment", "created", repo, comment: "/ask where is the greeting?"));
            await Route(context, Event("issue_comment", "created", repo, comment: "/ask and now?"));
            await Task.Delay(1500); // long enough for a child workflow that was wrongly started to show itself
            return 0;
        });

        var notice = Assert.Single(world.Snapshot(world.Notices)); // the same refusal on the same ticket is not repeated
        Assert.Equal(TicketComments.CommandRefused(verdict), notice);
        Assert.DoesNotContain("bob", notice);
        Assert.Empty(world.PrepareRequests);
        Assert.All(world.Snapshot(world.CommandChecks), check => Assert.Equal(("forgejo", "acme", repo, "bob"), (check.Host, check.Owner, check.Repo, check.Login)));
    }

    [SkippableFact]
    public async Task A_refusal_that_is_silent_says_nothing_at_all()
    {
        var world = new FakeWorld { CommandVerdict = new CommandVerdict(false, CommandRefusal.NotListed, "", true) };
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issue_comment", "created", repo, comment: "/ask where is the greeting?"));
            await Task.Delay(1000);
            return 0;
        });

        Assert.Empty(world.Snapshot(world.Notices));
        Assert.Empty(world.Snapshot(world.Comments));
        Assert.Single(world.Snapshot(world.CommandChecks));
    }

    [SkippableFact]
    public async Task An_allowed_command_is_started_as_before()
    {
        var world = new FakeWorld().Agent("asker", AgentReply.Says(Answers.Ask(files: "hello.py")));
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issue_comment", "created", repo, comment: "/ask where is the greeting?"));
            await WaitUntilAsync(() => world.Snapshot(world.PrepareRequests).Count > 0);
            return 0;
        });

        Assert.Single(world.Snapshot(world.CommandChecks));
        Assert.Empty(world.Snapshot(world.Notices));
    }

    [SkippableFact]
    public async Task A_label_that_starts_a_run_is_the_hosts_to_limit_so_nobody_is_checked_for_it()
    {
        var world = new FakeWorld { CommandVerdict = new CommandVerdict(false, CommandRefusal.Denied, "", false) };
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issues", "labeled", repo));
            return 0;
        });

        Assert.Empty(world.Snapshot(world.CommandChecks));
        Assert.Empty(world.Snapshot(world.Notices));
    }

    [SkippableFact]
    public async Task A_review_that_asks_for_changes_is_checked_by_its_reviewer_not_by_a_commenter()
    {
        var verdict = new CommandVerdict(false, CommandRefusal.TooLittleAccess, "write", false);
        var world = new FakeWorld { CommandVerdict = verdict };
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];
        var review = new WebhookDelivery(Guid.NewGuid().ToString(), "pull_request_rejected", JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            action = "reviewed",
            repository = new { full_name = $"acme/{repo}" },
            pull_request = new { number = 5, state = "open", head = new { @ref = "factory/issue-1" } },
            review = new { type = "pull_request_review_rejected", content = "Please change this." },
            sender = new { login = "mallory" },
        })).RootElement);

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, review);
            await Task.Delay(1500); // long enough for a revision that was wrongly started to show itself
            return 0;
        });

        var check = Assert.Single(world.Snapshot(world.CommandChecks));
        Assert.Equal(("forgejo", "acme", repo, "mallory"), (check.Host, check.Owner, check.Repo, check.Login));
        Assert.Equal(TicketComments.CommandRefused(verdict), Assert.Single(world.Snapshot(world.Notices)));
        Assert.DoesNotContain(world.Snapshot(world.Comments), comment => comment.Contains("review comment"));
    }

    [SkippableFact]
    public async Task The_check_is_told_which_commands_the_comment_carries()
    {
        var world = new FakeWorld { CommandVerdict = new CommandVerdict(false, CommandRefusal.TooLittleAccess, "admin", true) };
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];

        await world.RunAsync(fixture, async context =>
        {
            await Route(context, Event("issue_comment", "created", repo, comment: "Looks right.\n/option 2\n/hotfix"));
            await Route(context, Event("issue_comment", "created", repo, comment: "Yes, use the second one."));
            return 0;
        });

        var checks = world.Snapshot(world.CommandChecks);
        Assert.Equal(2, checks.Count);
        Assert.Equal(["option", "hotfix"], checks[0].Commands);
        Assert.Equal(["reply"], checks[1].Commands);
    }
}
