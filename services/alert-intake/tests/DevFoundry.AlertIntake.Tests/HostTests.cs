using Microsoft.Extensions.Configuration;

namespace DevFoundry.AlertIntake.Tests;

public sealed class HostTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    private static string Routes(string? host) =>
        $$"""{"routes":[{"match":{"service":"weather"},"repository":"acme/weather"{{(host is null ? "" : $",\"host\":\"{host}\"")}}}]}""";

    [Fact]
    public void A_route_without_a_host_goes_to_the_default_host_with_a_plain_owner_as_it_always_did()
    {
        var route = RouteTable.Parse(Routes(null)).Find(new Dictionary<string, string> { ["service"] = "weather" })!;

        Assert.Equal(("acme", "weather"), (route.Owner, route.Repo));
    }

    [Fact]
    public void A_route_can_name_another_configured_host_and_its_owner_then_carries_the_host()
    {
        var route = RouteTable.Parse(Routes("codeberg"), ["forgejo", "codeberg"]).Find(new Dictionary<string, string> { ["service"] = "weather" })!;

        Assert.Equal(("codeberg:acme", "weather"), (route.Owner, route.Repo));
    }

    [Theory]
    [InlineData("codeberg")]
    [InlineData("")]
    [InlineData("Not A Host")]
    public void A_route_naming_a_host_that_is_not_configured_is_refused_at_start(string host)
    {
        var error = Assert.Throws<InvalidOperationException>(() => RouteTable.Parse(Routes(host)));

        Assert.Contains("Route 1", error.Message);
        Assert.Contains("configured git host", error.Message);
    }

    [Fact]
    public void The_hosts_come_from_the_same_setting_the_factory_reads_and_a_route_without_a_host_uses_the_first()
    {
        var settings = IntakeSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ALERT_INTAKE_TOKEN"] = "0123456789abcdef",
            ["FACTORY_HOSTS"] = """[{"name":"github","type":"github","url":"https://github.com","token":"t1"},{"name":"codeberg","url":"https://codeberg.org","token":"t2"}]""",
        }).Build());

        Assert.Equal([new IntakeHost("github", "https://github.com", "t1"), new IntakeHost("codeberg", "https://codeberg.org", "t2")], settings.Hosts);
        var names = settings.Hosts.Select(host => host.Name).ToList();
        Assert.Equal("github:acme", RouteTable.Parse(Routes(null), names).Find(new Dictionary<string, string> { ["service"] = "weather" })!.Owner);
        Assert.Equal("codeberg:acme", RouteTable.Parse(Routes("codeberg"), names).Find(new Dictionary<string, string> { ["service"] = "weather" })!.Owner);
    }

    private sealed class Named(string name, List<string> log) : IForgejo
    {
        private Task<T> Done<T>(string call, T value)
        {
            log.Add($"{name}: {call}");
            return Task.FromResult(value);
        }

        public Task<int?> FindOpenTicketAsync(string owner, string repo, string key, CancellationToken ct) => Done<int?>($"find {owner}/{repo}", null);
        public Task<int?> FindRecentTicketAsync(string owner, string repo, string groupMarker, DateTimeOffset notBefore, CancellationToken ct) => Done<int?>($"recent {owner}/{repo}", null);
        public Task AddMarkerAsync(string owner, string repo, int number, string marker, CancellationToken ct) => Done($"marker {owner}/{repo}", 0);
        public Task<IssueRef> CreateTicketAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken ct) => Done($"create {owner}/{repo}", new IssueRef(1, ""));
        public Task CommentAsync(string owner, string repo, int number, string body, CancellationToken ct) => Done($"comment {owner}/{repo}", 0);
    }

    [Fact]
    public async Task Every_call_goes_to_the_host_of_the_owner_with_the_qualifier_taken_off()
    {
        var log = new List<string>();
        var client = new HostsClient(new Dictionary<string, IForgejo> { ["forgejo"] = new Named("main", log), ["codeberg"] = new Named("other", log) });
        var ct = CancellationToken.None;

        await client.FindOpenTicketAsync("acme", "w", "k", ct);
        await client.FindOpenTicketAsync("codeberg:acme", "w", "k", ct);
        await client.FindRecentTicketAsync("codeberg:acme", "w", "g", DateTimeOffset.UnixEpoch, ct);
        await client.AddMarkerAsync("codeberg:acme", "w", 1, "m", ct);
        await client.CreateTicketAsync("codeberg:acme", "w", "t", "b", [], ct);
        await client.CommentAsync("codeberg:acme", "w", 1, "c", ct);

        Assert.Equal(["main: find acme/w", "other: find acme/w", "other: recent acme/w", "other: marker acme/w", "other: create acme/w", "other: comment acme/w"], log);
    }

    [Fact]
    public async Task An_owner_on_a_host_that_is_not_configured_fails_and_reaches_no_host()
    {
        var log = new List<string>();
        var client = new HostsClient(new Dictionary<string, IForgejo> { ["forgejo"] = new Named("main", log) });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CommentAsync("nowhere:acme", "w", 1, "c", CancellationToken.None));

        Assert.Contains("nowhere", error.Message);
        Assert.Empty(log);
    }
}
