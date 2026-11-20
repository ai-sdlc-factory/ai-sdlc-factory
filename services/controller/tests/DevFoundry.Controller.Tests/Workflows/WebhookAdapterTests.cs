using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevFoundry.Controller.Tests;

/// <summary>Forgejo's adapter: how a delivery becomes the factory's own event. The router's own tests (EventRouterTests) cover the adapter and the router together.</summary>
public sealed class WebhookAdapterTests
{
    private static readonly ForgejoWebhookAdapter Adapter = new();
    private const string Delivery = "12345678-1234-1234-1234-123456789abc";

    private static RepoEvent Read(string eventName, object payload) =>
        Adapter.Normalise(new WebhookDelivery(Delivery, eventName, JsonSerializer.SerializeToElement(payload)));

    private static object Issue(string state = "open", object? pull = null, string[]? labels = null) => new
    {
        number = 7, title = "Add wishlist", body = "As a user...", state, user = new { login = "alice" }, pull_request = pull,
        labels = (labels ?? ["feature", "Bug"]).Select(name => new { name }).ToArray(),
    };

    [Theory]
    [InlineData("opened", RepoEventKind.TicketOpened)]
    [InlineData("labeled", RepoEventKind.TicketOpened)]
    [InlineData("label_updated", RepoEventKind.TicketOpened)]
    [InlineData("reopened", RepoEventKind.TicketReopened)]
    [InlineData("closed", RepoEventKind.TicketClosed)]
    [InlineData("edited", RepoEventKind.Other)]
    public void A_ticket_event_is_read_by_what_happened_to_the_ticket(string action, RepoEventKind kind)
    {
        var read = Read("issues", new { action, repository = new { full_name = "acme/shop" }, issue = Issue() });

        Assert.Equal(kind, read.Kind);
        Assert.Equal(("forgejo", "acme", "shop", 7, false, "issues", action), (read.Host, read.Owner, read.Repo, read.Number, read.IsPullRequest, read.EventName, read.Action));
        Assert.Equal(("Add wishlist", "As a user...", "alice", true), (read.Title, read.Body, read.Author, read.IsOpen));
        Assert.Equal(["feature", "Bug"], read.LabelNames);
        Assert.True(read.HasLabel("bug"));
        Assert.Equal("acme/shop", read.Repository);
    }

    [Fact]
    public void A_delivery_from_another_host_names_its_owner_with_that_host_and_the_default_hosts_stays_plain()
    {
        object payload = new { action = "opened", repository = new { full_name = "acme/shop" }, issue = Issue() };
        var element = JsonSerializer.SerializeToElement(payload);

        var other = Adapter.Normalise(new WebhookDelivery(Delivery, "issues", element, Host: "codeberg"));
        var plain = Adapter.Normalise(new WebhookDelivery(Delivery, "issues", element));

        Assert.Equal(("codeberg", "codeberg:acme", "acme/shop"), (other.Host, other.Owner, other.Repository));
        Assert.Equal(("forgejo", "acme"), (plain.Host, plain.Owner));
        var review = Adapter.Normalise(new WebhookDelivery(Delivery, "pull_request_rejected", JsonSerializer.SerializeToElement(new
        {
            repository = new { full_name = "acme/shop" }, pull_request = new { number = 9, head = new { @ref = "factory/issue-4" } },
        }), Host: "codeberg"));
        Assert.Equal("codeberg:acme", review.Owner);
    }

    [Fact]
    public void A_comment_carries_its_text_and_who_wrote_it()
    {
        var read = Read("issue_comment", new { action = "created", repository = new { full_name = "acme/shop" }, issue = Issue("closed"), comment = new { body = "/retry", user = new { login = "bob" } } });

        Assert.Equal(RepoEventKind.Commented, read.Kind);
        Assert.Equal(("/retry", "bob", false), (read.CommentBody, read.CommentAuthor, read.IsOpen));
    }

    [Theory]
    [InlineData("issue_comment")]
    [InlineData("pull_request_comment")]
    public void A_created_comment_on_a_pull_request_is_a_comment_on_a_pull_request_by_either_sign_of_it(string eventName)
    {
        var byObject = Read(eventName, new { action = "created", repository = new { full_name = "acme/shop" }, issue = Issue(pull: new { merged = false }), comment = new { body = "/fix", user = new { login = "bob" } } });
        var byFlag = Read(eventName, new { action = "created", repository = new { full_name = "acme/shop" }, issue = Issue(), is_pull = true, comment = new { body = "/fix", user = new { login = "bob" } } });

        Assert.All(new[] { byObject, byFlag }, read =>
        {
            Assert.Equal(RepoEventKind.Commented, read.Kind);
            Assert.True(read.IsPullRequest);
        });
    }

    [Fact]
    public void Other_activity_on_a_pull_request_is_nothing_to_do_and_a_pull_request_comment_event_on_a_plain_ticket_is_not_handled()
    {
        var edited = Read("issue_comment", new { action = "edited", repository = new { full_name = "acme/shop" }, issue = Issue(pull: new { merged = false }) });
        var plain = Read("pull_request_comment", new { action = "created", repository = new { full_name = "acme/shop" }, issue = Issue() });

        Assert.Equal(RepoEventKind.PullRequestActivity, edited.Kind);
        Assert.Equal(RepoEventKind.Other, plain.Kind);
    }

    [Theory]
    [InlineData("""{"repository":{"full_name":"acme/shop"}}""")]                                    // no ticket
    [InlineData("""{"repository":{"full_name":"shop"},"issue":{"number":7}}""")]                    // not owner/name
    [InlineData("""{"repository":{"full_name":"acme/shop"},"issue":{"number":"7"}}""")]             // no number
    [InlineData("""{"issue":{"number":7}}""")]                                                       // no repository
    [InlineData("""{"repository":"text","action":7}""")]                                            // wrong shapes
    public void A_delivery_that_is_not_about_a_ticket_is_unrelated(string json)
    {
        var read = Adapter.Normalise(new WebhookDelivery(Delivery, "issues", JsonDocument.Parse(json).RootElement));

        Assert.Equal(RepoEventKind.Unrelated, read.Kind);
    }

    [Fact]
    public void A_review_that_asks_for_changes_carries_the_pull_request_and_who_asked_under_either_event_name()
    {
        foreach (var name in new[] { "pull_request_rejected", "pull_request_review_rejected" })
        {
            var read = Read(name, new
            {
                action = "reviewed", repository = new { full_name = "acme/shop" }, sender = new { login = "carol" },
                pull_request = new { number = 9, state = "open", head = new { @ref = "factory/issue-4" } },
                review = new { content = "Rename it." },
            });

            Assert.Equal(RepoEventKind.ChangesRequested, read.Kind);
            Assert.Equal((9, "factory/issue-4", "open", "Rename it.", "carol", true), (read.Number, read.HeadRef, read.PullState, read.ReviewBody, read.Actor, read.IsPullRequest));
        }
    }

    [Fact]
    public void A_review_without_its_pull_request_is_malformed_and_a_missing_state_says_nothing()
    {
        Assert.Equal(RepoEventKind.MalformedReview, Read("pull_request_rejected", new { repository = new { full_name = "acme/shop" } }).Kind);
        var read = Read("pull_request_rejected", new { repository = new { full_name = "acme/shop" }, pull_request = new { number = 9, head = new { @ref = "x" } } });
        Assert.Null(read.PullState);
    }

    [Fact]
    public void The_receipt_fields_are_kept_even_for_a_delivery_the_factory_does_not_route()
    {
        var read = Adapter.Normalise(new WebhookDelivery(Delivery, "push", JsonDocument.Parse("""{"repository":{"full_name":"acme/shop"},"ref":"refs/heads/main"}""").RootElement));

        Assert.Equal((RepoEventKind.Unrelated, "acme/shop", "refs/heads/main", null), (read.Kind, read.Repository, read.Reference, read.Action));
    }

    // ---- the signature and the headers ------------------------------------------------------------------

    private const string Secret = "0123456789abcdef0123456789abcdef";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");

    private static Func<string, string> Headers(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(pair => pair.Name == name).Value ?? "";

    private static string Sign(string secret) => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Body));

    [Fact]
    public void The_signature_is_checked_against_the_exact_bytes_and_the_secret()
    {
        var good = Headers(("X-Forgejo-Signature", Sign(Secret)));

        Assert.True(Adapter.HasWellFormedSignature(good));
        Assert.True(Adapter.IsValidSignature(good, Body, Secret));
        Assert.False(Adapter.IsValidSignature(good, [.. Body, 0x20], Secret));
        Assert.False(Adapter.IsValidSignature(Headers(("X-Forgejo-Signature", Sign("another-secret-another-secret-123"))), Body, Secret));
        Assert.False(Adapter.HasWellFormedSignature(Headers()));
        Assert.False(Adapter.IsValidSignature(Headers(("X-Forgejo-Signature", "abc")), Body, Secret));
    }

    [Fact]
    public void The_event_name_and_the_delivery_id_must_be_of_a_safe_shape()
    {
        Assert.Equal("issue_comment", Adapter.EventName(Headers(("X-Forgejo-Event", "issue_comment"))));
        Assert.Null(Adapter.EventName(Headers(("X-Forgejo-Event", "Issue Comment\n"))));
        Assert.Null(Adapter.EventName(Headers()));
        Assert.Equal(Delivery, Adapter.DeliveryId(Headers(("X-Forgejo-Delivery", Delivery.ToUpperInvariant()))));
        Assert.Null(Adapter.DeliveryId(Headers(("X-Forgejo-Delivery", "not-a-guid"))));
    }

    [Fact]
    public void Hosts_are_found_by_name_and_an_unknown_one_is_not_there()
    {
        Assert.Same(Adapter.GetType(), WebhookAdapters.Find("Forgejo")!.GetType());
        Assert.Equal("forgejo", Adapter.Type);
        Assert.Null(WebhookAdapters.Find("github"));
    }
}

/// <summary>The router on the factory's own events: no host is involved, so the same decisions hold for every adapter.</summary>
public sealed class NeutralRouterTests
{
    private static RepoEvent Ticket(RepoEventKind kind, string[]? labels = null, bool open = true, string comment = "", bool pull = false) =>
        new("test-host", "d1", "whatever", kind, Owner: "acme", Repo: "shop", Number: 7, IsPullRequest: pull, IsOpen: open,
            Title: "Add wishlist", Body: "As a user...", Author: "alice", Labels: labels ?? ["feature"], CommentBody: comment, CommentAuthor: "bob");

    [Fact]
    public void The_same_ticket_number_on_two_hosts_is_two_runs()
    {
        var onDefault = EventRouter.Decide(Ticket(RepoEventKind.TicketOpened) with { Owner = "acme" });
        var onOther = EventRouter.Decide(Ticket(RepoEventKind.TicketOpened) with { Owner = "codeberg:acme" });

        Assert.NotEqual(onDefault.Feature!.WorkflowId, onOther.Feature!.WorkflowId);
        Assert.Equal("codeberg:acme", onOther.Feature.Owner);
    }

    [Fact]
    public void An_open_ticket_with_the_label_starts_a_feature_whatever_host_it_came_from()
    {
        var decision = EventRouter.Decide(Ticket(RepoEventKind.TicketOpened));

        Assert.Equal(RoutingAction.StartFeature, decision.Action);
        Assert.False(decision.Restart);
        Assert.Equal(("acme", "shop", 7, "Add wishlist", "alice"), (decision.Feature!.Owner, decision.Feature.Repo, decision.Feature.IssueNumber, decision.Feature.Title, decision.Feature.Author));
        Assert.True(EventRouter.Decide(Ticket(RepoEventKind.TicketReopened)).Restart);
    }

    [Fact]
    public void A_ticket_without_a_label_or_a_closed_one_starts_nothing()
    {
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Ticket(RepoEventKind.TicketOpened, labels: ["question"])).Action);
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Ticket(RepoEventKind.TicketOpened, open: false)).Action);
    }

    [Fact]
    public void Commands_in_a_comment_are_read_from_the_comment_text_and_the_comments_author()
    {
        var ask = EventRouter.Decide(Ticket(RepoEventKind.Commented, labels: [], comment: "/ask how does it work?"));
        var fix = EventRouter.Decide(Ticket(RepoEventKind.Commented, pull: true, comment: "/fix rename it"));

        Assert.Equal((RoutingAction.AskQuestion, "bob", "how does it work?"), (ask.Action, ask.Ask!.Author, ask.Ask.Question));
        Assert.Equal((RoutingAction.RevisePullRequest, "rename it"), (fix.Action, fix.Revision!.Instruction));
    }

    [Fact]
    public void The_factorys_own_comment_is_never_answered()
    {
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Ticket(RepoEventKind.Commented, comment: $"{EventRouter.BotMarker}\n/retry")).Action);
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Ticket(RepoEventKind.Commented, pull: true, comment: $"{EventRouter.BotMarker}\n/fix")).Action);
    }

    [Theory]
    [InlineData(RepoEventKind.Unrelated, "not a ticket event")]
    [InlineData(RepoEventKind.PullRequestActivity, "pull request")]
    [InlineData(RepoEventKind.MalformedReview, "not a pull request review")]
    [InlineData(RepoEventKind.Other, "unhandled whatever/")]
    public void Events_the_factory_has_no_use_for_are_ignored_with_a_reason(RepoEventKind kind, string reason)
    {
        var decision = EventRouter.Decide(Ticket(kind));

        Assert.Equal((RoutingAction.Ignore, reason), (decision.Action, decision.Reason));
    }

    [Fact]
    public void A_review_asking_for_changes_revises_only_an_open_pull_request_on_a_factory_branch()
    {
        RepoEvent Review(string? head = "factory/issue-4", string? state = "open", string content = "Rename it.") =>
            new("test-host", "d1", "whatever", RepoEventKind.ChangesRequested, Owner: "acme", Repo: "shop", Number: 9, IsPullRequest: true, HeadRef: head, PullState: state, ReviewBody: content, Actor: "carol");

        var decision = EventRouter.Decide(Review());
        Assert.Equal((RoutingAction.RevisePullRequest, 9, "Rename it.", "carol"), (decision.Action, decision.Revision!.PullNumber, decision.Revision.Instruction, decision.Revision.Author));
        Assert.Equal(RoutingAction.RevisePullRequest, EventRouter.Decide(Review(state: null)).Action);
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Review(head: "feature/x")).Action);
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Review(head: null)).Action);
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Review(state: "closed")).Action);
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Review(content: $"{EventRouter.BotMarker} automatic")).Action);
    }
}
