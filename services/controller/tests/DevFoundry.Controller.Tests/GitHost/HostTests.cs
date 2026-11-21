using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace DevFoundry.Controller.Tests;

public sealed class RepoNamesTests
{
    [Theory]
    [InlineData("forgejo", "acme", "acme")]
    [InlineData("codeberg", "acme", "codeberg:acme")]
    public void The_default_hosts_owners_stay_plain_and_any_others_carry_their_host(string host, string owner, string expected)
    {
        Assert.Equal(expected, RepoNames.Qualify(host, owner));
        Assert.Equal((host, owner), RepoNames.Split(expected));
    }

    [Theory]
    [InlineData("acme", "forgejo", "acme")]
    [InlineData("codeberg:acme", "codeberg", "acme")]
    [InlineData(":acme", "forgejo", ":acme")]
    public void An_owner_without_a_qualifier_belongs_to_the_default_host(string qualified, string host, string owner) =>
        Assert.Equal((host, owner), RepoNames.Split(qualified));

    [Theory]
    [InlineData("codeberg", true)]
    [InlineData("a", true)]
    [InlineData("my-host-2", true)]
    [InlineData("", false)]
    [InlineData("2host", false)]
    [InlineData("Host", false)]
    [InlineData("a:b", false)]
    [InlineData("a/b", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void A_host_name_is_short_and_plain_because_it_ends_up_in_routes_ids_and_folders(string name, bool ok) =>
        Assert.Equal(ok, RepoNames.IsHostName(name));

    [Fact]
    public void Two_hosts_can_never_share_a_run_a_checkout_or_a_count_even_for_the_same_owner_and_repository()
    {
        var plain = WorkIds.For("acme", "shop", 7);
        var other = WorkIds.For(RepoNames.Qualify("codeberg", "acme"), "shop", 7);

        Assert.NotEqual(plain, other);
        Assert.Matches(@"\A[a-z0-9][a-z0-9._-]{0,127}\z", other); // still a valid workspace name
        Assert.Equal(WorkIds.For("acme", "shop", 7), plain);       // and the default host's ids are the ones that always were
    }
}

public sealed class HostsSettingTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    private static string Host(string name = "codeberg", string? type = null, string url = "https://codeberg.org/", string secret = Secret, string token = "tok", string? login = null) =>
        JsonSerializer.Serialize(new { name, type, url, token, bot_login = login, webhook_secret = secret });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("[]")]
    public void No_host_at_all_stops_the_start_and_shows_what_to_write(string? json) =>
        Assert.Contains("at least one", Assert.Throws<InvalidOperationException>(() => HostsSetting.Parse(json)).Message);

    [Fact]
    public void A_host_is_read_with_defaults_for_its_kind_and_its_bot_and_a_trailing_slash_is_dropped()
    {
        var host = Assert.Single(HostsSetting.Parse($"[{Host()}]"));

        Assert.Equal(new HostDefinition("codeberg", "forgejo", "https://codeberg.org", "tok", "factory-bot", Secret, TriggerRules.Default), host);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Anything_but_an_array_stops_the_start(string json) =>
        Assert.Contains("JSON array", Assert.Throws<InvalidOperationException>(() => HostsSetting.Parse(json)).Message);

    [Theory]
    [InlineData("Codeberg")]
    [InlineData("")]
    [InlineData("a:b")]
    public void A_host_needs_a_valid_name_of_its_own(string name) =>
        Assert.Contains("name of its own", Assert.Throws<InvalidOperationException>(() => HostsSetting.Parse($"[{Host(name)}]")).Message);

    [Fact]
    public void Two_hosts_cannot_share_a_name() =>
        Assert.Contains("name of its own", Assert.Throws<InvalidOperationException>(() => HostsSetting.Parse($"[{Host()},{Host()}]")).Message);

    [Fact]
    public void A_kind_without_an_adapter_an_unusable_address_and_a_short_secret_are_refused_without_echoing_the_secret()
    {
        Assert.Contains("no adapter", Assert.Throws<InvalidOperationException>(() => HostsSetting.Parse($"[{Host(type: "gitlab")}]")).Message);
        Assert.Contains("http or https", Assert.Throws<InvalidOperationException>(() => HostsSetting.Parse($"[{Host(url: "ftp://x")}]")).Message);
        var error = Assert.Throws<InvalidOperationException>(() => HostsSetting.Parse($"[{Host(secret: "short-secret-123")}]", needWebhookSecret: true));
        Assert.Contains("webhook_secret", error.Message);
        Assert.DoesNotContain("short-secret-123", error.Message);
    }

    [Fact]
    public void The_controller_and_the_intake_need_no_webhook_secret_but_the_api_does()
    {
        const string json = """[{"name":"github","type":"github","url":"https://github.com","token":"t"}]""";

        Assert.Single(HostsSetting.Parse(json));
        Assert.Contains("webhook_secret", Assert.Throws<InvalidOperationException>(() => HostsSetting.Parse(json, needWebhookSecret: true)).Message);
    }

    // ---- what the controller makes of them

    private static WorkerSettings Settings(string? hosts) =>
        WorkerSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FACTORY_HOSTS"] = hosts }).Build());

    private static string Forgejo() => Host("forgejo", url: "http://forgejo:3000", token: "default-token", login: "default-bot");

    [Fact]
    public void The_hosts_come_in_the_order_they_are_listed_each_with_its_own_bot_and_token()
    {
        var settings = Settings($"[{Forgejo()},{Host(login: "cb-bot")}]");

        Assert.Equal(["forgejo", "codeberg"], settings.Hosts.Select(host => host.Name));
        Assert.Equal(new HostSettings("forgejo", "forgejo", "http://forgejo:3000", "default-token", "default-bot", TriggerRules.Default), settings.Hosts[0]);
        Assert.Equal("default-bot", settings.BotLoginFor("acme"));
        Assert.Equal("cb-bot", settings.BotLoginFor("codeberg:acme"));
        Assert.Equal(["default-token", "tok"], settings.HostTokens);
    }

    [Fact]
    public void A_repository_on_a_host_that_is_not_listed_fails_for_good()
    {
        var settings = Settings($"[{Forgejo()}]");

        Assert.Single(settings.Hosts);
        var error = Assert.Throws<Temporalio.Exceptions.ApplicationFailureException>(() => settings.HostFor("nowhere:acme"));
        Assert.Contains("nowhere", error.Message);
        Assert.True(error.NonRetryable);
    }

    [Fact]
    public void A_factory_without_a_forgejo_still_works_with_every_owner_qualified()
    {
        var settings = Settings("""[{"name":"github","type":"github","url":"https://github.com","token":"t","bot_login":"vacant"}]""");

        Assert.Equal("vacant", settings.BotLoginFor("github:acme"));
        Assert.Equal("github:acme", RepoNames.Qualify("github", "acme"));
    }

    [Fact]
    public void A_setting_that_cannot_be_used_stops_the_controller_at_start() =>
        Assert.Throws<InvalidOperationException>(() => Settings("[{\"name\":\"x\"}]"));
}

public sealed class GitHostRouterTests
{
    private sealed class Recording(string name) : IGitHost
    {
        public List<string> Calls { get; } = [];

        private Task<T> Done<T>(string call, T value)
        {
            Calls.Add(call);
            return Task.FromResult(value);
        }

        public Task PostIssueCommentAsync(string owner, string repo, int issueNumber, string body, CancellationToken ct) => Done($"{name}: comment {owner}/{repo}#{issueNumber}", 0);
        public Task<IReadOnlyList<string>> RecentCommentBodiesAsync(string owner, string repo, int issueNumber, int count, CancellationToken ct) => Done<IReadOnlyList<string>>($"{name}: bodies {owner}/{repo}", []);
        public Task<bool> PostNoticeOnceAsync(string owner, string repo, int issueNumber, string body, CancellationToken ct) => Done($"{name}: notice {owner}/{repo}", true);
        public Task<bool> PostNoticeUniqueAsync(string owner, string repo, int issueNumber, string body, CancellationToken ct) => Done($"{name}: unique {owner}/{repo}", true);
        public Task<IssueInfo> CreateIssueAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labelNames, CancellationToken ct) => Done($"{name}: issue {owner}/{repo}", new IssueInfo(1, ""));
        public Task<PullRequestDetails> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct) => Done($"{name}: pull {owner}/{repo}#{number}", new PullRequestDetails(number, "", "", "open", false, "", "", "", "", "", ""));
        public Task<PullChanges> GetPullChangesAsync(string owner, string repo, int number, CancellationToken ct) => Done($"{name}: changes {owner}/{repo}", new PullChanges([], 0, "", false));
        public Task<IReadOnlyList<ReviewDetails>> GetUnaddressedReviewsAsync(string owner, string repo, int number, string botLogin, DateTimeOffset? since, CancellationToken ct) => Done<IReadOnlyList<ReviewDetails>>($"{name}: reviews {owner}/{repo} as {botLogin}", []);
        public Task<RevisionHistory> GetRevisionHistoryAsync(string owner, string repo, int number, CancellationToken ct) => Done($"{name}: history {owner}/{repo}", new RevisionHistory(0, null));
        public Task<bool> RequestReviewAsync(string owner, string repo, int number, IReadOnlyList<string> reviewers, CancellationToken ct) => Done($"{name}: request {owner}/{repo}", true);
        public Task<int?> FindFactoryPullRequestAsync(string owner, string repo, int ticket, CancellationToken ct) => Done<int?>($"{name}: find {owner}/{repo}#{ticket}", 5);
        public Task<IReadOnlyList<PullSummary>> ListAllPullRequestsAsync(string owner, string repo, CancellationToken ct) => Done<IReadOnlyList<PullSummary>>($"{name}: all {owner}/{repo}", []);
        public Task<PullRequestInfo> CreatePullRequestAsync(string owner, string repo, string head, string baseBranch, string title, string body, CancellationToken ct) => Done($"{name}: create {owner}/{repo}", new PullRequestInfo(1, ""));
        public Task<IReadOnlyList<(string Owner, string Repo)>> ListWritableRepositoriesAsync(CancellationToken ct) => Done<IReadOnlyList<(string Owner, string Repo)>>($"{name}: list", [("acme", "shop")]);
        public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string owner, string repo, CancellationToken ct) => Done<IReadOnlyList<BranchInfo>>($"{name}: branches {owner}/{repo}", []);
        public Task<bool> IssueIsClosedAsync(string owner, string repo, int number, CancellationToken ct) => Done($"{name}: closed {owner}/{repo}#{number}", false);
        public Task<IReadOnlySet<string>> OpenPullRequestBranchesAsync(string owner, string repo, CancellationToken ct) => Done<IReadOnlySet<string>>($"{name}: heads {owner}/{repo}", new HashSet<string>());
        public Task DeleteBranchAsync(string owner, string repo, string branch, CancellationToken ct) => Done($"{name}: delete {owner}/{repo} {branch}", 0);
    }

    private readonly Recording main = new("main");
    private readonly Recording other = new("other");

    private GitHostRouter Router() => new(new Dictionary<string, IGitHost> { ["forgejo"] = main, ["codeberg"] = other });

    [Fact]
    public async Task A_call_goes_to_the_host_its_owner_belongs_to_with_the_qualifier_taken_off()
    {
        var router = Router();

        await router.PostIssueCommentAsync("acme", "shop", 7, "hi", CancellationToken.None);
        await router.PostIssueCommentAsync("codeberg:acme", "shop", 7, "hi", CancellationToken.None);
        var found = await router.FindFactoryPullRequestAsync("codeberg:acme", "shop", 3, CancellationToken.None);

        Assert.Equal(["main: comment acme/shop#7"], main.Calls);
        Assert.Equal(["other: comment acme/shop#7", "other: find acme/shop#3"], other.Calls);
        Assert.Equal(5, found);
    }

    [Fact]
    public async Task Every_operation_is_forwarded_to_the_owners_host()
    {
        var router = Router();
        const string owner = "codeberg:acme";
        var ct = CancellationToken.None;

        await router.RecentCommentBodiesAsync(owner, "shop", 1, 5, ct);
        await router.PostNoticeOnceAsync(owner, "shop", 1, "x", ct);
        await router.PostNoticeUniqueAsync(owner, "shop", 1, "x", ct);
        await router.CreateIssueAsync(owner, "shop", "t", "b", [], ct);
        await router.GetPullRequestAsync(owner, "shop", 1, ct);
        await router.GetPullChangesAsync(owner, "shop", 1, ct);
        await router.GetUnaddressedReviewsAsync(owner, "shop", 1, "bot", null, ct);
        await router.GetRevisionHistoryAsync(owner, "shop", 1, ct);
        await router.RequestReviewAsync(owner, "shop", 1, [], ct);
        await router.ListAllPullRequestsAsync(owner, "shop", ct);
        await router.CreatePullRequestAsync(owner, "shop", "h", "main", "t", "b", ct);
        await router.ListBranchesAsync(owner, "shop", ct);
        await router.IssueIsClosedAsync(owner, "shop", 1, ct);
        await router.OpenPullRequestBranchesAsync(owner, "shop", ct);
        await router.DeleteBranchAsync(owner, "shop", "b", ct);

        Assert.Equal(15, other.Calls.Count);
        Assert.All(other.Calls, call => Assert.Contains("acme/shop", call));
        Assert.DoesNotContain(other.Calls, call => call.Contains("codeberg"));
        Assert.Empty(main.Calls);
    }

    [Fact]
    public async Task Listing_repositories_asks_every_host_and_qualifies_the_owners_of_all_but_the_default()
    {
        var all = await Router().ListWritableRepositoriesAsync(CancellationToken.None);

        Assert.Equal([("acme", "shop"), ("codeberg:acme", "shop")], all);
    }

    [Fact]
    public async Task A_repository_on_a_host_that_is_not_configured_fails_for_good_and_reaches_no_host()
    {
        var error = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() => Router().GetPullRequestAsync("nowhere:acme", "shop", 1, CancellationToken.None));

        Assert.True(error.NonRetryable);
        Assert.Empty(main.Calls);
        Assert.Empty(other.Calls);
    }
}
