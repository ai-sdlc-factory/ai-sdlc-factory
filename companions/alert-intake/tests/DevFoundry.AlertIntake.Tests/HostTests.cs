using Microsoft.Extensions.Configuration;

namespace DevFoundry.AlertIntake.Tests;

public sealed class HostTests
{
    private static string Routes(string? host) =>
        $$"""{"routes":[{"match":{"service":"weather"},"repository":"acme/weather"{{(host is null ? "" : $",\"host\":\"{host}\"")}}}]}""";

    [Fact]
    public void A_route_without_a_host_goes_to_the_first_host()
    {
        var route = RouteTable.Parse(Routes(null)).Find(new Dictionary<string, string> { ["service"] = "weather" })!;

        Assert.Equal(("forgejo", "acme", "weather"), (route.Host, route.Owner, route.Repo));
    }

    [Fact]
    public void A_route_can_name_another_configured_host()
    {
        var route = RouteTable.Parse(Routes("codeberg"), ["forgejo", "codeberg"]).Find(new Dictionary<string, string> { ["service"] = "weather" })!;

        Assert.Equal(("codeberg", "acme", "weather"), (route.Host, route.Owner, route.Repo));
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
    public void The_hosts_are_read_from_their_own_setting_and_a_route_without_a_host_uses_the_first()
    {
        var settings = IntakeSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ALERT_INTAKE_TOKEN"] = "0123456789abcdef",
            ["ALERT_INTAKE_HOSTS"] = """[{"name":"github","type":"github","url":"https://github.com/","token":"t1"},{"name":"codeberg","url":"https://codeberg.org","token":"t2"}]""",
        }).Build());

        Assert.Equal([new IntakeHost("github", "github", "https://github.com", "t1"), new IntakeHost("codeberg", "forgejo", "https://codeberg.org", "t2")], settings.Hosts);
        var names = settings.Hosts.Select(host => host.Name).ToList();
        Assert.Equal("github", RouteTable.Parse(Routes(null), names).Find(new Dictionary<string, string> { ["service"] = "weather" })!.Host);
        Assert.Equal("codeberg", RouteTable.Parse(Routes("codeberg"), names).Find(new Dictionary<string, string> { ["service"] = "weather" })!.Host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void No_usable_list_stops_the_start_and_shows_what_to_write(string? json) =>
        Assert.Contains("ALERT_INTAKE_HOSTS", Assert.Throws<InvalidOperationException>(() => HostList.Parse(json)).Message);

    [Theory]
    [InlineData("""[{"name":"Bad Name","url":"http://x"}]""", "name of its own")]
    [InlineData("""[{"name":"a","url":"http://x"},{"name":"a","url":"http://y"}]""", "name of its own")]
    [InlineData("""[{"name":"a","type":"gitlab","url":"http://x"}]""", "no client")]
    [InlineData("""[{"name":"a","url":"ftp://x"}]""", "http or https")]
    public void A_host_that_cannot_be_used_is_refused_and_named(string json, string message)
    {
        var error = Assert.Throws<InvalidOperationException>(() => HostList.Parse(json));

        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Each_kind_of_host_makes_the_client_of_its_own_and_reaches_its_own_api()
    {
        Assert.IsType<ForgejoClient>(TicketHostKinds.CreateClient(new IntakeHost("a", "forgejo", "http://forgejo:3000/", "t")));
        Assert.IsType<GitHubClient>(TicketHostKinds.CreateClient(new IntakeHost("b", "github", "https://github.com", "t")));
        Assert.Equal("https://api.github.com", TicketHostKinds.Find("github")!.ApiBase("https://github.com"));
        Assert.Equal("https://git.example.com/api/v3", TicketHostKinds.Find("github")!.ApiBase("https://git.example.com"));
        Assert.Equal("http://forgejo:3000", TicketHostKinds.Find("forgejo")!.ApiBase("http://forgejo:3000"));
        Assert.Null(TicketHostKinds.Find("gitlab"));
    }

    private sealed class Named(string name, List<string> log) : ITicketHost
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
    public async Task A_ticket_goes_to_the_host_its_route_names_and_to_no_other()
    {
        var log = new List<string>();
        var hosts = new TicketHosts(new Dictionary<string, ITicketHost> { ["forgejo"] = new Named("main", log), ["codeberg"] = new Named("other", log) });

        await hosts.For("forgejo").FindOpenTicketAsync("acme", "w", "k", CancellationToken.None);
        await hosts.For("codeberg").CreateTicketAsync("acme", "w", "t", "b", [], CancellationToken.None);

        Assert.Equal(["main: find acme/w", "other: create acme/w"], log);
    }

    [Fact]
    public void A_host_that_is_not_configured_fails_and_reaches_no_host()
    {
        var log = new List<string>();
        var hosts = new TicketHosts(new Dictionary<string, ITicketHost> { ["forgejo"] = new Named("main", log) });

        var error = Assert.Throws<InvalidOperationException>(() => hosts.For("nowhere"));

        Assert.Contains("nowhere", error.Message);
        Assert.Empty(log);
    }
}
