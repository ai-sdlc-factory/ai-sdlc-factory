using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiSdlcFactory.Controller.Tests;

/// <summary>GitHub's webhook adapter, on payloads shaped as GitHub sends them.</summary>
public sealed class GitHubAdapterTests
{
    private static readonly GitHubWebhookAdapter Adapter = new();
    private const string Delivery = "12345678-1234-1234-1234-123456789abc";

    private static RepoEvent Read(string eventName, object payload, string host = "github") =>
        Adapter.Normalise(new WebhookDelivery(Delivery, eventName, JsonSerializer.SerializeToElement(payload), host, "github"));

    private static object Issue(string state = "open", object? pull = null, string[]? labels = null, string? type = null) => new
    {
        number = 12, title = "Add wishlist", body = "As a user...", state, user = new { login = "alice" }, pull_request = pull,
        labels = (labels ?? ["feature"]).Select(name => new { name, color = "ededed" }).ToArray(),
        type = type is null ? null : new { name = type },
    };

    [Theory]
    [InlineData("opened", RepoEventKind.TicketOpened)]
    [InlineData("labeled", RepoEventKind.TicketOpened)]
    [InlineData("reopened", RepoEventKind.TicketReopened)]
    [InlineData("closed", RepoEventKind.TicketClosed)]
    [InlineData("unlabeled", RepoEventKind.Other)]
    [InlineData("edited", RepoEventKind.Other)]
    [InlineData("assigned", RepoEventKind.Other)]
    public void An_issues_event_is_read_by_what_happened_to_the_ticket(string action, RepoEventKind kind)
    {
        var read = Read("issues", new { action, repository = new { full_name = "acme/shop" }, issue = Issue(), sender = new { login = "alice" } });

        Assert.Equal(kind, read.Kind);
        Assert.Equal(("github", "github:acme", "shop", 12, false), (read.Host, read.Owner, read.Repo, read.Number, read.IsPullRequest));
        Assert.Equal(("Add wishlist", "As a user...", "alice", true), (read.Title, read.Body, read.Author, read.IsOpen));
        Assert.Equal(["feature"], read.LabelNames);
        Assert.Null(read.ItemType);
    }

    [Fact]
    public void A_ticket_with_an_issue_type_carries_it_as_its_work_item_type()
    {
        var read = Read("issues", new { action = "opened", repository = new { full_name = "acme/shop" }, issue = Issue(type: "Bug", labels: []) });

        Assert.Equal("Bug", read.ItemType);
        Assert.Equal("bug", new TriggerRules(ItemTypes: new Dictionary<string, IReadOnlyList<string>> { ["bug"] = ["Bug"] }).KindOf(read));
    }

    [Fact]
    public void A_comment_on_a_ticket_and_a_comment_on_a_pull_request_are_told_apart_by_the_pull_request_part_of_the_issue()
    {
        object Comment(object issue) => new { action = "created", repository = new { full_name = "acme/shop" }, issue, comment = new { body = "/fix rename it", user = new { login = "bob" } } };

        var onTicket = Read("issue_comment", Comment(Issue()));
        var onPull = Read("issue_comment", Comment(Issue(pull: new { url = "https://api.github.com/repos/acme/shop/pulls/12" })));

        Assert.Equal((RepoEventKind.Commented, false, "/fix rename it", "bob"), (onTicket.Kind, onTicket.IsPullRequest, onTicket.CommentBody, onTicket.CommentAuthor));
        Assert.Equal((RepoEventKind.Commented, true), (onPull.Kind, onPull.IsPullRequest));
    }

    [Fact]
    public void Other_activity_on_a_pull_request_and_a_comment_that_was_edited_are_nothing_to_do()
    {
        var edited = Read("issue_comment", new { action = "edited", repository = new { full_name = "acme/shop" }, issue = Issue(pull: new { }) });

        Assert.Equal(RepoEventKind.PullRequestActivity, edited.Kind);
    }

    private static object Review(string state, string action = "submitted", string head = "factory/issue-12") => new
    {
        action, repository = new { full_name = "acme/shop" }, sender = new { login = "carol" },
        pull_request = new { number = 15, state = "open", head = new { @ref = head } },
        review = new { state, body = "Rename it.", user = new { login = "carol" } },
    };

    [Theory]
    [InlineData("changes_requested", true)]
    [InlineData("CHANGES_REQUESTED", true)]
    [InlineData("commented", false)]
    [InlineData("approved", false)]
    public void Only_a_submitted_review_that_asks_for_changes_starts_a_revision(string state, bool asks)
    {
        var read = Read("pull_request_review", Review(state));

        Assert.Equal(asks ? RepoEventKind.ChangesRequested : RepoEventKind.PullRequestActivity, read.Kind);
        Assert.Equal((15, "factory/issue-12", "open", "Rename it.", "carol", "github:acme"), (read.Number, read.HeadRef, read.PullState, read.ReviewBody, read.Actor, read.Owner));
    }

    [Fact]
    public void An_edited_or_dismissed_review_and_a_review_without_its_pull_request_are_not_revisions()
    {
        Assert.Equal(RepoEventKind.PullRequestActivity, Read("pull_request_review", Review("changes_requested", action: "dismissed")).Kind);
        Assert.Equal(RepoEventKind.PullRequestActivity, Read("pull_request_review", Review("changes_requested", action: "edited")).Kind);
        Assert.Equal(RepoEventKind.MalformedReview, Read("pull_request_review", new { action = "submitted", repository = new { full_name = "a/b" } }).Kind);
    }

    [Theory]
    [InlineData("ping")]
    [InlineData("push")]
    [InlineData("pull_request")]
    [InlineData("installation")]
    public void Events_without_a_ticket_are_unrelated(string eventName)
    {
        var read = Read(eventName, new { zen = "Keep it logically awesome.", repository = new { full_name = "acme/shop" }, pull_request = new { number = 3 } });

        Assert.Equal(RepoEventKind.Unrelated, read.Kind);
    }

    [Fact]
    public void The_router_decides_on_a_github_ticket_like_on_any_other()
    {
        var read = Read("issues", new { action = "opened", repository = new { full_name = "acme/shop" }, issue = Issue() });

        var decision = EventRouter.Decide(read);

        Assert.Equal((RoutingAction.StartFeature, "github:acme", "shop", 12), (decision.Action, decision.Feature!.Owner, decision.Feature.Repo, decision.Feature.IssueNumber));
        Assert.NotEqual(WorkIds.For("acme", "shop", 12), decision.Feature.WorkflowId); // never the same run as a repository of that name elsewhere
    }

    // ---- the signature and the headers

    private const string Secret = "0123456789abcdef0123456789abcdef";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");

    private static Func<string, string> Headers(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(pair => pair.Name == name).Value ?? "";

    private static string Sign(string secret) => "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Body));

    [Fact]
    public void The_signature_is_the_sha256_prefix_and_the_hmac_of_the_exact_bytes()
    {
        var good = Headers(("X-Hub-Signature-256", Sign(Secret)));

        Assert.True(Adapter.HasWellFormedSignature(good));
        Assert.True(Adapter.IsValidSignature(good, Body, Secret));
        Assert.False(Adapter.IsValidSignature(good, [.. Body, 0x20], Secret));
        Assert.False(Adapter.IsValidSignature(Headers(("X-Hub-Signature-256", Sign("another-secret-another-secret-123"))), Body, Secret));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha256=abc")]
    [InlineData("sha1=0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")] // a bare digest has no prefix
    public void A_signature_that_is_not_sha256_and_a_digest_is_not_well_formed(string header)
    {
        Assert.False(Adapter.HasWellFormedSignature(Headers(("X-Hub-Signature-256", header))));
        Assert.False(Adapter.IsValidSignature(Headers(("X-Hub-Signature-256", header)), Body, Secret));
    }

    [Fact]
    public void The_event_and_delivery_headers_are_githubs_and_must_be_of_a_safe_shape()
    {
        Assert.Equal("issue_comment", Adapter.EventName(Headers(("X-GitHub-Event", "issue_comment"))));
        Assert.Null(Adapter.EventName(Headers(("X-GitHub-Event", "Issue Comment"))));
        Assert.Null(Adapter.EventName(Headers(("X-Forgejo-Event", "issue_comment"))));
        Assert.Equal(Delivery, Adapter.DeliveryId(Headers(("X-GitHub-Delivery", Delivery))));
        Assert.Null(Adapter.DeliveryId(Headers(("X-GitHub-Delivery", "not-a-guid"))));
        Assert.Equal(("github", "X-GitHub-Event", "X-GitHub-Delivery"), (Adapter.Type, Adapter.EventHeader, Adapter.DeliveryHeader));
    }

    [Fact]
    public void The_registry_knows_both_kinds_and_a_host_of_either_kind_can_be_configured()
    {
        Assert.NotNull(WebhookAdapters.Find("github"));
        Assert.NotNull(WebhookAdapters.Find("forgejo"));
        var host = Assert.Single(HostsSetting.Parse($$$"""[{"name":"github","type":"github","url":"https://github.com","token":"t","bot_login":"acme","webhook_secret":"{{{Secret}}}"}]"""));
        Assert.Equal("github", host.Type);
    }
}
