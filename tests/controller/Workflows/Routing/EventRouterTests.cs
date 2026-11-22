using System.Text.Json;

namespace AiSdlcFactory.Controller.Tests;

public sealed class EventRouterTests
{
    private const string Delivery = "12345678-1234-1234-1234-123456789abc";

    /// <summary>A Forgejo delivery as the router gets it: read by Forgejo's adapter, so these tests cover the adapter and the router together.</summary>
    private static RepoEvent Event(string name, string payloadJson) =>
        new ForgejoWebhookAdapter().Normalise(new WebhookDelivery(Delivery, name, JsonDocument.Parse(payloadJson).RootElement));

    /// <summary>Builds a Forgejo-shaped ticket payload; <paramref name="more"/> adds top-level fields.</summary>
    private static string Ticket(
        string action, string[]? labels = null, string state = "open", object? more = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["repository"] = new { full_name = "acme/shop" },
            ["issue"] = new
            {
                number = 7,
                title = "Add wishlist",
                body = "As a user...",
                state,
                user = new { login = "alice" },
                pull_request = (object?)null, // present and null on real tickets
                labels = (labels ?? ["feature"]).Select(name => new { name }).ToArray(),
            },
        };
        foreach (var property in more?.GetType().GetProperties() ?? [])
        {
            payload[property.Name] = property.GetValue(more);
        }
        return JsonSerializer.Serialize(payload);
    }

    private static object Comment(string body, string login = "bob") => new { body, user = new { login } };

    [Theory]
    [InlineData("opened")]
    [InlineData("label_updated")]
    [InlineData("reopened")]
    public void Open_ticket_with_the_feature_label_starts_a_feature(string action)
    {
        var decision = EventRouter.Decide(Event("issues", Ticket(action)));

        Assert.Equal(RoutingAction.StartFeature, decision.Action);
        var feature = decision.Feature!;
        Assert.Equal(("acme", "shop", 7, "Add wishlist", "As a user...", "alice"),
            (feature.Owner, feature.Repo, feature.IssueNumber, feature.Title, feature.Body, feature.Author));
        Assert.Equal(["feature"], feature.Labels);
        Assert.Equal(WorkIds.For("acme", "shop", 7), feature.WorkflowId);
    }

    [Fact]
    public void Forgejos_real_event_name_for_a_review_that_requests_changes_is_routed()
    {
        var decision = EventRouter.Decide(Event("pull_request_rejected", ReviewPayload()));

        Assert.Equal(RoutingAction.RevisePullRequest, decision.Action);
    }

    [Fact]
    public void Only_reopening_a_ticket_restarts_a_finished_feature()
    {
        Assert.True(EventRouter.Decide(Event("issues", Ticket("reopened"))).Restart);
        Assert.False(EventRouter.Decide(Event("issues", Ticket("opened"))).Restart);
        Assert.False(EventRouter.Decide(Event("issues", Ticket("label_updated"))).Restart);
    }

    [Fact]
    public void Label_match_ignores_case()
    {
        var decision = EventRouter.Decide(Event("issues", Ticket("opened", ["Feature"])));
        Assert.Equal(RoutingAction.StartFeature, decision.Action);
    }

    [Theory]
    [InlineData("question", "open")]
    [InlineData("", "open")]
    [InlineData("feature", "closed")]
    [InlineData("bug", "closed")]
    public void Other_tickets_are_ignored(string label, string state)
    {
        var decision = EventRouter.Decide(Event("issues", Ticket("opened", label == "" ? [] : [label], state)));
        Assert.Equal(RoutingAction.Ignore, decision.Action);
    }

    [Fact]
    public void Edits_do_not_restart_a_feature()
    {
        var decision = EventRouter.Decide(Event("issues", Ticket("edited")));
        Assert.Equal(RoutingAction.Ignore, decision.Action);
    }

    [Fact]
    public void Closing_a_feature_ticket_closes_its_workflow()
    {
        var decision = EventRouter.Decide(Event("issues", Ticket("closed", state: "closed")));
        Assert.Equal(RoutingAction.CloseFeature, decision.Action);
        Assert.Equal(WorkIds.For("acme", "shop", 7), decision.Feature!.WorkflowId);
    }

    [Fact]
    public void Human_comment_on_a_feature_is_a_reply()
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment("Use the existing cart.") })));

        Assert.Equal(RoutingAction.ReplyToFeature, decision.Action);
        Assert.Equal(WorkIds.For("acme", "shop", 7), decision.Feature!.WorkflowId);
        Assert.Equal(new HumanReply("bob", "Use the existing cart."), decision.Reply);
    }

    [Theory]
    [InlineData("/retry")]
    [InlineData("  /RETRY  ")]
    [InlineData("Docker was down.\n/retry")]
    public void Retry_on_an_open_ticket_restarts_its_run(string body)
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment(body) })));

        Assert.Equal(RoutingAction.RetryFeature, decision.Action);
        Assert.True(decision.Restart);
        Assert.Equal(WorkIds.For("acme", "shop", 7), decision.Feature!.WorkflowId);
        Assert.False(string.IsNullOrEmpty(decision.Feature.Title)); // the ticket's current text goes with it
        Assert.True(decision.Feature.Resume); // carries on from the stages an earlier run finished
    }

    [Theory]
    [InlineData("/retry restart")]
    [InlineData("  /RETRY   Restart  ")]
    [InlineData("Start over please.\n/retry restart")]
    public void Retry_restart_starts_from_the_beginning(string body)
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment(body) })));

        Assert.Equal(RoutingAction.RetryFeature, decision.Action);
        Assert.True(decision.Restart);
        Assert.False(decision.Feature!.Resume);
    }

    [Fact]
    public void Reopening_a_ticket_still_starts_from_the_beginning()
    {
        var decision = EventRouter.Decide(Event("issues", Ticket("reopened")));

        Assert.True(decision.Restart);
        Assert.False(decision.Feature!.Resume);
    }

    [Theory]
    [InlineData("please /retry this")]
    [InlineData("/retry-later")]
    [InlineData("/retryy")]
    [InlineData("/retry now")]
    [InlineData("/retry restarting")]
    public void Only_a_line_that_is_just_retry_counts(string body)
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment(body) })));
        Assert.Equal(RoutingAction.ReplyToFeature, decision.Action);
    }

    [Fact]
    public void Retry_on_a_closed_ticket_or_from_the_factory_itself_does_nothing()
    {
        var closed = EventRouter.Decide(Event("issue_comment",
            Ticket("created", state: "closed", more: new { comment = Comment("/retry") })));
        var own = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment($"{EventRouter.BotMarker}\n/retry", "factory-bot") })));

        Assert.Equal(RoutingAction.Ignore, closed.Action);
        Assert.Equal(RoutingAction.Ignore, own.Action);
    }

    [Theory]
    [InlineData("/qa", null, "focused", null)]
    [InlineData("  /QA  ", null, "focused", null)]
    [InlineData("/qa api", "api", "focused", null)]
    [InlineData("/qa UI", "ui", "focused", null)]
    [InlineData("Ready for testing.\n/qa ui", "ui", "focused", null)]
    [InlineData("/qa full", null, "full", null)]
    [InlineData("/qa full api", "api", "full", null)]
    [InlineData("/qa api full", "api", "full", null)]
    [InlineData("/qa focus on the 404 cases", null, "focused", "focus on the 404 cases")]
    [InlineData("/qa api look at validation\nand the empty city", "api", "focused", "look at validation\nand the empty city")]
    [InlineData("/qa full check the redis fallback too", null, "full", "check the redis fallback too")]
    [InlineData("/qa full full", null, "full", "full")]
    public void Qa_on_a_feature_ticket_starts_a_qa_run_for_that_ticket(string body, string? suite, string mode, string? note)
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment(body) })));

        Assert.Equal(RoutingAction.RunQa, decision.Action);
        Assert.Equal(new QaRequest("acme", "shop", 7, false, suite, "bob", mode, note), decision.Qa);
    }

    [Theory]
    [InlineData("please /qa")]
    [InlineData("/qapi")]
    [InlineData("/qa-later")]
    public void Anything_but_a_qa_command_at_the_start_of_a_line_is_an_ordinary_comment(string body)
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment(body) })));
        Assert.Equal(RoutingAction.ReplyToFeature, decision.Action);
    }

    [Fact]
    public void A_very_long_note_is_cut()
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment("/qa " + new string('x', 5000)) })));

        Assert.Equal(QaCommand.MaxNote, decision.Qa!.Note!.Length);
    }

    [Fact]
    public void Qa_is_for_feature_and_bug_tickets_only_and_never_from_the_factorys_own_comments()
    {
        var bug = EventRouter.Decide(Event("issue_comment", Ticket("created", ["bug"], more: new { comment = Comment("/qa") })));
        var incident = EventRouter.Decide(Event("issue_comment", Ticket("created", ["incident"], more: new { comment = Comment("/qa") })));
        var question = EventRouter.Decide(Event("issue_comment", Ticket("created", ["question"], more: new { comment = Comment("/qa") })));
        var own = EventRouter.Decide(Event("issue_comment", Ticket("created", more: new { comment = Comment($"{EventRouter.BotMarker}\n/qa", "factory-bot") })));

        Assert.Equal(RoutingAction.RunQa, bug.Action);
        Assert.Equal(RoutingAction.Ignore, incident.Action);
        Assert.Equal(RoutingAction.Ignore, question.Action);
        Assert.Equal(RoutingAction.Ignore, own.Action);
    }

    [Fact]
    public void The_factorys_own_comments_are_ignored()
    {
        var body = $"{EventRouter.BotMarker}\nQuestion for you";
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment(body, "factory-bot") })));
        Assert.Equal(RoutingAction.Ignore, decision.Action);
    }

    [Fact]
    public void Comments_on_tickets_that_are_neither_features_nor_bugs_are_ignored()
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", ["question"], more: new { comment = Comment("hi") })));
        Assert.Equal(RoutingAction.Ignore, decision.Action);
    }

    [Fact]
    public void A_bug_ticket_starts_a_bug_run_with_its_own_workflow_id()
    {
        var decision = EventRouter.Decide(Event("issues", Ticket("opened", ["bug"])));

        Assert.Equal(RoutingAction.StartFeature, decision.Action);
        Assert.True(decision.Feature!.IsBug);
        Assert.Equal(WorkIds.Bug, decision.Feature.Kind);
        Assert.StartsWith("bug-acme-shop-7-", decision.Feature.WorkflowId);
    }

    [Fact]
    public void A_ticket_with_both_labels_is_built_as_a_feature()
    {
        var decision = EventRouter.Decide(Event("issues", Ticket("opened", ["bug", "feature"])));

        Assert.False(decision.Feature!.IsBug);
        Assert.StartsWith("feature-acme-shop-7-", decision.Feature.WorkflowId);
    }

    [Fact]
    public void Comments_and_closing_on_a_bug_ticket_go_to_the_bug_run()
    {
        var comment = EventRouter.Decide(Event("issue_comment", Ticket("created", ["bug"], more: new { comment = Comment("here is the stack trace") })));
        var closed = EventRouter.Decide(Event("issues", Ticket("closed", ["bug"], "closed")));

        Assert.Equal(RoutingAction.ReplyToFeature, comment.Action);
        Assert.StartsWith("bug-acme-shop-7-", comment.Feature!.WorkflowId);
        Assert.Equal(RoutingAction.CloseFeature, closed.Action);
        Assert.StartsWith("bug-acme-shop-7-", closed.Feature!.WorkflowId);
    }

    private static string ReviewPayload(string headRef = "factory/issue-3", string state = "open", string content = "Please rename these.", string reviewer = "bob") =>
        JsonSerializer.Serialize(new
        {
            action = "reviewed",
            repository = new { full_name = "acme/shop" },
            pull_request = new { number = 7, state, head = new { @ref = headRef } },
            review = new { type = "pull_request_review_rejected", content },
            sender = new { login = reviewer },
        });

    [Fact]
    public void A_review_that_requests_changes_on_a_factory_branch_starts_a_revision()
    {
        var decision = EventRouter.Decide(Event("pull_request_review_rejected", ReviewPayload()));

        Assert.Equal(RoutingAction.RevisePullRequest, decision.Action);
        var revision = decision.Revision!;
        Assert.Equal(("acme", "shop", 7, RevisionRequest.ByReview, "Please rename these.", "bob"), (revision.Owner, revision.Repo, revision.PullNumber, revision.Trigger, revision.Instruction, revision.Author));
        Assert.StartsWith("revision-acme-shop-7-", revision.WorkflowId);
    }

    [Theory]
    [InlineData("feature/other", "open")]
    [InlineData("main", "open")]
    [InlineData("factory/issue-3-wip", "open")]
    [InlineData("factory/issue-3", "closed")]
    public void A_review_on_anything_but_an_open_factory_branch_is_ignored(string branch, string state) =>
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("pull_request_review_rejected", ReviewPayload(branch, state))).Action);

    [Fact]
    public void A_review_written_by_the_factory_is_ignored() =>
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("pull_request_review_rejected", ReviewPayload(content: $"{EventRouter.BotMarker}\nsomething"))).Action);

    [Theory]
    [InlineData("""{"repository":{"full_name":"acme/shop"}}""")]
    [InlineData("""{"repository":{"full_name":"acme/shop"},"pull_request":"text"}""")]
    [InlineData("[]")]
    public void A_review_event_of_an_unexpected_shape_is_ignored_without_failing(string payload) =>
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("pull_request_review_rejected", payload)).Action);

    private static string PullComment(string body, string eventUser = "bob") =>
        Ticket("created", [], more: new { comment = new { body, user = new { login = eventUser } }, is_pull = true }).Replace("\"pull_request\":null", "\"pull_request\":{\"merged\":false}");

    [Theory]
    [InlineData("/qa", null, "focused", null)]
    [InlineData("/qa api", "api", "focused", null)]
    [InlineData("Looks done.\n/qa ui", "ui", "focused", null)]
    [InlineData("/qa full ui the settings page", "ui", "full", "the settings page")]
    public void Qa_on_a_pull_request_starts_a_qa_run_for_that_pull_request(string body, string? suite, string mode, string? note)
    {
        var decision = EventRouter.Decide(Event("issue_comment", PullComment(body)));

        Assert.Equal(RoutingAction.RunQa, decision.Action);
        Assert.Equal(new QaRequest("acme", "shop", 7, true, suite, "bob", mode, note), decision.Qa);
    }

    [Fact]
    public void Fix_qa_on_a_pull_request_is_a_revision_whose_instruction_is_qa()
    {
        var decision = EventRouter.Decide(Event("issue_comment", PullComment("/fix qa")));

        Assert.Equal(RoutingAction.RevisePullRequest, decision.Action);
        Assert.Equal("qa", decision.Revision!.Instruction);
    }

    [Theory]
    [InlineData("issue_comment")]
    [InlineData("pull_request_comment")]
    public void The_fix_command_on_a_pull_request_starts_a_revision_with_what_follows_it(string eventName)
    {
        var decision = EventRouter.Decide(Event(eventName, PullComment("Looks good overall.\n/fix rename the helper\nand add a test")));

        Assert.Equal(RoutingAction.RevisePullRequest, decision.Action);
        Assert.Equal(RevisionRequest.ByCommand, decision.Revision!.Trigger);
        Assert.Equal("rename the helper\nand add a test", decision.Revision.Instruction);
        Assert.Equal("bob", decision.Revision.Author);
        Assert.Equal(7, decision.Revision.PullNumber);
    }

    [Theory]
    [InlineData("/revise just this")]
    [InlineData("  /FIX  shout")]
    public void The_command_has_an_alias_and_forgives_case_and_spaces(string body) =>
        Assert.Equal(RoutingAction.RevisePullRequest, EventRouter.Decide(Event("issue_comment", PullComment(body))).Action);

    [Fact]
    public void The_fix_command_alone_is_a_request_to_act_on_the_latest_review()
    {
        var decision = EventRouter.Decide(Event("issue_comment", PullComment("/fix")));

        Assert.Equal(RoutingAction.RevisePullRequest, decision.Action);
        Assert.Equal("", decision.Revision!.Instruction);
    }

    [Theory]
    [InlineData("I think we should /fix this later")]
    [InlineData("please fix this")]
    [InlineData("/fixing the build")]
    [InlineData("/option 1")]
    public void Any_other_comment_on_a_pull_request_does_nothing(string body) =>
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("issue_comment", PullComment(body))).Action);

    [Theory]
    [InlineData("/update")]
    [InlineData("  /UPDATE  ")]
    [InlineData("Behind main again.\n/update\nthanks")]
    [InlineData("/update\r\n")]
    public void Update_on_a_pull_request_starts_an_update_of_that_pull_request(string body)
    {
        var decision = EventRouter.Decide(Event("issue_comment", PullComment(body)));

        Assert.Equal(RoutingAction.UpdatePullRequest, decision.Action);
        Assert.Equal(new UpdateRequest("acme", "shop", 7, true, "bob"), decision.Update);
    }

    [Theory]
    [InlineData("/update now please")]
    [InlineData("/updates")]
    [InlineData("please /update")]
    [InlineData("update")]
    public void Anything_but_the_command_alone_on_a_line_is_not_an_update(string body) =>
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("issue_comment", PullComment(body))).Action);

    [Theory]
    [InlineData("/review")]
    [InlineData("  /REVIEW  ")]
    [InlineData("Could you look at this?\n/review\nthanks")]
    [InlineData("/review\r\n")]
    public void Review_on_a_pull_request_starts_a_review_of_that_pull_request(string body)
    {
        var decision = EventRouter.Decide(Event("issue_comment", PullComment(body)));

        Assert.Equal(RoutingAction.ReviewPullRequest, decision.Action);
        Assert.Equal(new PullReviewRequest("acme", "shop", 7, "bob"), decision.Review);
    }

    [Theory]
    [InlineData("/review please")]
    [InlineData("/reviews")]
    [InlineData("please /review")]
    [InlineData("/revise this")]
    public void Anything_but_the_review_command_alone_on_a_line_is_not_a_review(string body) =>
        Assert.NotEqual(RoutingAction.ReviewPullRequest, EventRouter.Decide(Event("issue_comment", PullComment(body))).Action);

    [Fact]
    public void A_review_command_written_by_the_factory_is_ignored() =>
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("issue_comment", PullComment($"{EventRouter.BotMarker}\n/review"))).Action);

    [Fact]
    public void Review_on_a_ticket_is_only_a_comment()
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", ["feature"], more: new { comment = Comment("/review") })));

        Assert.Equal(RoutingAction.ReplyToFeature, decision.Action);
    }

    [Fact]
    public void A_ticket_with_the_docs_label_starts_a_documentation_run()
    {
        var decision = EventRouter.Decide(Event("issues", Ticket("opened", ["docs"])));

        Assert.Equal(RoutingAction.StartFeature, decision.Action);
        Assert.Equal(WorkIds.Docs, decision.Feature!.Kind);
        Assert.True(decision.Feature.IsDocs);
        Assert.Equal(WorkIds.For("acme", "shop", 7, WorkIds.Docs), decision.Feature.WorkflowId);
    }

    [Theory]
    [InlineData("feature", WorkIds.Feature)]
    [InlineData("bug", WorkIds.Bug)]
    [InlineData("plan", WorkIds.Plan)]
    [InlineData("incident", WorkIds.Incident)]
    public void The_docs_label_loses_to_every_other_kind_of_ticket(string other, string kind)
    {
        var decision = EventRouter.Decide(Event("issues", Ticket("opened", ["docs", other])));

        Assert.Equal(kind, decision.Feature!.Kind);
    }

    [Fact]
    public void Qa_and_update_need_a_feature_or_bug_ticket_so_a_docs_ticket_gets_neither()
    {
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("issue_comment", Ticket("created", ["docs"], more: new { comment = Comment("/qa") }))).Action);
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("issue_comment", Ticket("created", ["docs"], more: new { comment = Comment("/update") }))).Action);
    }

    [Fact]
    public void An_update_command_written_by_the_factory_is_ignored() =>
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("issue_comment", PullComment($"{EventRouter.BotMarker}\n/update"))).Action);

    [Theory]
    [InlineData("feature")]
    [InlineData("bug")]
    public void Update_on_a_feature_or_bug_ticket_asks_for_its_pull_request_to_be_updated(string label)
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", [label], more: new { comment = Comment("/update") })));

        Assert.Equal(RoutingAction.UpdatePullRequest, decision.Action);
        Assert.Equal(new UpdateRequest("acme", "shop", 7, false, "bob"), decision.Update);
    }

    [Theory]
    [InlineData("incident")]
    [InlineData("plan")]
    public void Update_on_an_incident_or_plan_ticket_does_nothing(string label) =>
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("issue_comment", Ticket("created", [label], more: new { comment = Comment("/update") }))).Action);

    [Fact]
    public void Update_on_a_ticket_without_a_label_does_nothing() =>
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("issue_comment", Ticket("created", [], more: new { comment = Comment("/update") }))).Action);

    [Fact]
    public void A_fix_command_written_by_the_factory_is_ignored() =>
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("issue_comment", PullComment($"{EventRouter.BotMarker}\n/fix this"))).Action);

    [Fact]
    public void A_fix_command_on_an_ordinary_ticket_is_still_just_a_human_comment()
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", ["feature"], more: new { comment = Comment("/fix this") })));

        Assert.Equal(RoutingAction.ReplyToFeature, decision.Action);
    }

    [Fact]
    public void An_incident_ticket_starts_an_incident_run_and_the_incident_label_wins_over_the_others()
    {
        foreach (var labels in new[] { new[] { "incident" }, ["incident", "feature"], ["bug", "incident"] })
        {
            var decision = EventRouter.Decide(Event("issues", Ticket("opened", labels)));

            Assert.Equal(RoutingAction.StartFeature, decision.Action);
            Assert.True(decision.Feature!.IsIncident);
            Assert.StartsWith("incident-acme-shop-7-", decision.Feature.WorkflowId);
        }
    }

    [Fact]
    public void Comments_and_closing_on_an_incident_ticket_go_to_the_incident_run()
    {
        var comment = EventRouter.Decide(Event("issue_comment", Ticket("created", ["incident"], more: new { comment = Comment("/option 1") })));
        var closed = EventRouter.Decide(Event("issues", Ticket("closed", ["incident"], "closed")));

        Assert.StartsWith("incident-acme-shop-7-", comment.Feature!.WorkflowId);
        Assert.Equal(RoutingAction.CloseFeature, closed.Action);
        Assert.StartsWith("incident-acme-shop-7-", closed.Feature!.WorkflowId);
    }

    [Fact]
    public void A_plan_ticket_starts_a_plan_run_and_the_incident_label_alone_wins_over_it()
    {
        foreach (var labels in new[] { new[] { "plan" }, ["plan", "feature"], ["bug", "plan"] })
        {
            var decision = EventRouter.Decide(Event("issues", Ticket("opened", labels)));

            Assert.Equal(RoutingAction.StartFeature, decision.Action);
            Assert.True(decision.Feature!.IsPlan);
            Assert.StartsWith("plan-acme-shop-7-", decision.Feature.WorkflowId);
        }
        Assert.True(EventRouter.Decide(Event("issues", Ticket("opened", ["plan", "incident"]))).Feature!.IsIncident);
    }

    [Fact]
    public void Comments_and_closing_on_a_plan_ticket_go_to_the_plan_run_and_qa_is_not_offered()
    {
        var comment = EventRouter.Decide(Event("issue_comment", Ticket("created", ["plan"], more: new { comment = Comment("/approve 2") })));
        var closed = EventRouter.Decide(Event("issues", Ticket("closed", ["plan"], "closed")));
        var qa = EventRouter.Decide(Event("issue_comment", Ticket("created", ["plan"], more: new { comment = Comment("/qa") })));

        Assert.Equal(RoutingAction.ReplyToFeature, comment.Action);
        Assert.StartsWith("plan-acme-shop-7-", comment.Feature!.WorkflowId);
        Assert.Equal(RoutingAction.CloseFeature, closed.Action);
        Assert.StartsWith("plan-acme-shop-7-", closed.Feature!.WorkflowId);
        Assert.Equal(RoutingAction.Ignore, qa.Action);
    }

    [Fact]
    public void Feature_and_bug_ids_for_the_same_ticket_number_differ()
    {
        Assert.NotEqual(WorkIds.For("acme", "shop", 7), WorkIds.For("acme", "shop", 7, WorkIds.Bug));
    }

    [Fact]
    public void Pull_request_activity_is_ignored()
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment("lgtm"), is_pull = true })));
        Assert.Equal(RoutingAction.Ignore, decision.Action);
    }

    [Fact]
    public void Issue_with_a_pull_request_object_is_ignored()
    {
        var payload = JsonSerializer.Serialize(new
        {
            action = "opened",
            repository = new { full_name = "acme/shop" },
            issue = new
            {
                number = 7, state = "open", labels = new[] { new { name = "feature" } },
                pull_request = new { merged = false },
            },
        });
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event("issues", payload)).Action);
    }

    [Theory]
    [InlineData("push", """{"ref":"refs/heads/main","repository":{"full_name":"acme/shop"}}""")]
    [InlineData("issues", """{"action":"opened"}""")]
    [InlineData("issues", """{"action":"opened","repository":{"full_name":"no-slash"},"issue":{"number":1}}""")]
    [InlineData("issues", """{"action":"opened","repository":"text","issue":7}""")]
    [InlineData("issues", """[]""")]
    public void Unexpected_shapes_are_ignored_without_failing(string name, string payload) =>
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Event(name, payload)).Action);

    [Theory]
    [InlineData("Acme", "Shop App", 3, "feature-acme-shop-app-3-")]
    [InlineData("a/b", "c", 1, "feature-a-b-c-1-")]
    public void Workflow_ids_are_readable_and_safe(string owner, string repo, int number, string prefix)
    {
        var id = WorkIds.For(owner, repo, number);
        Assert.StartsWith(prefix, id);
        Assert.Matches(@"^[a-z0-9][a-z0-9._-]{0,127}$", id);
        Assert.Equal(id, WorkIds.For(owner, repo, number));
    }

    [Theory]
    [InlineData("a-b", "c", "a", "b-c")]
    [InlineData("a.b", "c", "a-b", "c")]
    [InlineData("a_b", "c", "a-b", "c")]
    public void Repositories_whose_cleaned_names_look_alike_get_different_ids(string owner1, string repo1, string owner2, string repo2) =>
        Assert.NotEqual(WorkIds.For(owner1, repo1, 1), WorkIds.For(owner2, repo2, 1));

    [Fact]
    public void Case_does_not_split_a_repository()
    {
        Assert.Equal(WorkIds.For("Acme", "Shop", 1), WorkIds.For("acme", "shop", 1));
    }

    [Fact]
    public void Very_long_names_still_fit_and_stay_different()
    {
        var first = WorkIds.For(new string('o', 40), new string('r', 100) + "1", int.MaxValue);
        var second = WorkIds.For(new string('o', 40), new string('r', 100) + "2", int.MaxValue);
        Assert.InRange(first.Length, 1, 128);
        Assert.Matches(@"^[a-z0-9][a-z0-9._-]{0,127}$", first);
        Assert.NotEqual(first, second);
    }

    // ---- /qa go ---------------------------------------------------------------------------------------------

    [Fact]
    public void Qa_go_on_a_ticket_is_the_go_ahead_and_not_a_new_run_with_the_note_go()
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", more: new { comment = Comment("/qa go") })));

        Assert.Equal(RoutingAction.ConfirmQa, decision.Action);
        Assert.Equal(WorkIds.For("acme", "shop", 7, WorkIds.Qa), decision.Qa!.WorkflowId);
    }

    [Fact]
    public void Qa_go_on_a_pull_request_is_the_go_ahead_for_that_pull_request()
    {
        var decision = EventRouter.Decide(Event("issue_comment", PullComment("/qa go")));

        Assert.Equal(RoutingAction.ConfirmQa, decision.Action);
        Assert.True(decision.Qa!.OnPullRequest);
    }

    // ---- /setup ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_setup_comment_on_a_ticket_with_no_label_starts_a_setup()
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", labels: [], more: new { comment = Comment("/setup") })));

        Assert.Equal(RoutingAction.RunSetup, decision.Action);
        var setup = decision.Setup!;
        Assert.Equal(("acme", "shop", 7, WorkIds.Setup, "bob"), (setup.Owner, setup.Repo, setup.IssueNumber, setup.Kind, setup.Author));
        Assert.Equal(WorkIds.For("acme", "shop", 7, WorkIds.Setup), setup.WorkflowId);
    }

    [Fact]
    public void A_sentence_that_mentions_setup_is_not_the_command()
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", labels: [], more: new { comment = Comment("can you /setup this please") })));

        Assert.NotEqual(RoutingAction.RunSetup, decision.Action);
    }

    // ---- /ask --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_question_on_a_ticket_with_a_factory_label_starts_an_answer_and_is_not_a_reply_to_an_agent()
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", more: new { comment = Comment("/ask how does the cart work?") })));

        Assert.Equal(RoutingAction.AskQuestion, decision.Action);
        var ask = decision.Ask!;
        Assert.Equal(("acme", "shop", 7, false, "bob", "how does the cart work?", "Add wishlist", "As a user..."),
            (ask.Owner, ask.Repo, ask.Number, ask.OnPullRequest, ask.Author, ask.Question, ask.Title, ask.Body));
        Assert.Equal(WorkIds.For("acme", "shop", 7, WorkIds.Ask), ask.WorkflowId);
        Assert.Null(decision.Reply);
    }

    [Fact]
    public void A_question_needs_no_factory_label()
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", labels: [], more: new { comment = Comment("/ask what is this repository?") })));

        Assert.Equal(RoutingAction.AskQuestion, decision.Action);
    }

    [Fact]
    public void A_question_on_a_closed_ticket_is_still_answered()
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", state: "closed", more: new { comment = Comment("/ask what changed?") })));

        Assert.Equal(RoutingAction.AskQuestion, decision.Action);
    }

    [Fact]
    public void A_question_on_a_pull_request_starts_an_answer_for_any_pull_request()
    {
        var decision = EventRouter.Decide(Event("issue_comment", PullComment("/ask does this break the API?")));

        Assert.Equal(RoutingAction.AskQuestion, decision.Action);
        Assert.True(decision.Ask!.OnPullRequest);
        Assert.Equal("does this break the API?", decision.Ask.Question);
    }

    [Fact]
    public void A_bare_ask_is_still_a_command()
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", more: new { comment = Comment("/ask") })));

        Assert.Equal(RoutingAction.AskQuestion, decision.Action);
        Assert.Equal("", decision.Ask!.Question);
    }

    [Fact]
    public void A_question_written_by_the_factory_is_ignored()
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", more: new { comment = Comment($"{EventRouter.BotMarker}\n/ask loop forever") })));

        Assert.Equal(RoutingAction.Ignore, decision.Action);
    }

    [Fact]
    public void A_comment_that_only_mentions_ask_is_a_normal_comment()
    {
        var decision = EventRouter.Decide(Event("issue_comment", Ticket("created", more: new { comment = Comment("I would ask the team first.") })));

        Assert.Equal(RoutingAction.ReplyToFeature, decision.Action);
    }
}
