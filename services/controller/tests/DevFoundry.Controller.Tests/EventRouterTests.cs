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
        Assert.Equal("feature-acme-shop-7", feature.WorkflowId);
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
    [InlineData("bug", "open")]
    [InlineData("", "open")]
    [InlineData("feature", "closed")]
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
        Assert.Equal("feature-acme-shop-7", decision.Feature!.WorkflowId);
    }

    [Fact]
    public void Human_comment_on_a_feature_is_a_reply()
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", more: new { comment = Comment("Use the existing cart.") })));

        Assert.Equal(RoutingAction.ReplyToFeature, decision.Action);
        Assert.Equal("feature-acme-shop-7", decision.Feature!.WorkflowId);
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
    public void Comments_on_non_feature_tickets_are_ignored()
    {
        var decision = EventRouter.Decide(Event("issue_comment",
            Ticket("created", ["bug"], more: new { comment = Comment("hi") })));
        Assert.Equal(RoutingAction.Ignore, decision.Action);
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
    [InlineData("Acme", "Shop App", 3, "feature-acme-shop-app-3")]
    [InlineData("a/b", "c", 1, "feature-a-b-c-1")]
    public void Workflow_ids_are_stable_and_safe(string owner, string repo, int number, string expected) =>
        Assert.Equal(expected, FeatureIds.For(owner, repo, number));
}
