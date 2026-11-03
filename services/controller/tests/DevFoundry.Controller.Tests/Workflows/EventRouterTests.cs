using System.Text.Json;

namespace DevFoundry.Controller.Tests;

public sealed class EventRouterTests
{
    private const string Delivery = "12345678-1234-1234-1234-123456789abc";

    private static ForgejoEvent Event(string name, string payloadJson) =>
        new(Delivery, name, JsonDocument.Parse(payloadJson).RootElement);

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
        Assert.Equal(FeatureIds.For("acme", "shop", 7), feature.WorkflowId);
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
        Assert.Equal(FeatureIds.For("acme", "shop", 7), decision.Feature!.WorkflowId);
    }

    [Fact]
    public void Human_comment_on_a_feature_is_a_reply()
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment("Use the existing cart.") })));

        Assert.Equal(RoutingAction.ReplyToFeature, decision.Action);
        Assert.Equal(FeatureIds.For("acme", "shop", 7), decision.Feature!.WorkflowId);
        Assert.Equal(new HumanReply("bob", "Use the existing cart."), decision.Reply);
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
        Assert.Equal(FeatureIds.Bug, decision.Feature.Kind);
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
    public void Feature_and_bug_ids_for_the_same_ticket_number_differ()
    {
        Assert.NotEqual(FeatureIds.For("acme", "shop", 7), FeatureIds.For("acme", "shop", 7, FeatureIds.Bug));
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
        var id = FeatureIds.For(owner, repo, number);
        Assert.StartsWith(prefix, id);
        Assert.Matches(@"^[a-z0-9][a-z0-9._-]{0,127}$", id);
        Assert.Equal(id, FeatureIds.For(owner, repo, number));
    }

    [Theory]
    [InlineData("a-b", "c", "a", "b-c")]
    [InlineData("a.b", "c", "a-b", "c")]
    [InlineData("a_b", "c", "a-b", "c")]
    public void Repositories_whose_cleaned_names_look_alike_get_different_ids(string owner1, string repo1, string owner2, string repo2) =>
        Assert.NotEqual(FeatureIds.For(owner1, repo1, 1), FeatureIds.For(owner2, repo2, 1));

    [Fact]
    public void Case_does_not_split_a_repository()
    {
        Assert.Equal(FeatureIds.For("Acme", "Shop", 1), FeatureIds.For("acme", "shop", 1));
    }

    [Fact]
    public void Very_long_names_still_fit_and_stay_different()
    {
        var first = FeatureIds.For(new string('o', 40), new string('r', 100) + "1", int.MaxValue);
        var second = FeatureIds.For(new string('o', 40), new string('r', 100) + "2", int.MaxValue);
        Assert.InRange(first.Length, 1, 128);
        Assert.Matches(@"^[a-z0-9][a-z0-9._-]{0,127}$", first);
        Assert.NotEqual(first, second);
    }
}
