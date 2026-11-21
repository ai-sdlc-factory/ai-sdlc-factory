using Microsoft.Extensions.Configuration;

namespace DevFoundry.AlertIntake.Tests;

public sealed class EvidenceTests
{
    private const string EvidenceRoutes = """
        {"routes":[{"match":{"service":"weather"},"repository":"valters/weather","labels":["incident"],
          "evidence":[{"title":"Recent errors","query":"{app=\"api\"} |= \"error\""},{"title":"Quiet","query":"{app=\"none\"}"}]}]}
        """;

    [Fact]
    public async Task The_routes_log_queries_add_redacted_bounded_lines_to_the_ticket()
    {
        var host = new FakeTicketHost();
        var logs = new FakeLogs();
        logs.Lines["{app=\"api\"} |= \"error\""] = Enumerable.Range(1, 40).Select(i => $"error {i} password=hunter{i} ```boom``` " + new string('z', 300)).ToArray();

        await Setup.Processor(host, routes: EvidenceRoutes, logs: logs).ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing())), default);

        var body = Assert.Single(host.Tickets).Body.Replace("\r", "");
        Assert.Equal(["{app=\"api\"} |= \"error\"", "{app=\"none\"}"], logs.Queries); // only what the operator's file asks for
        Assert.Contains("**Recent errors**\n```text\n", body);
        Assert.Contains("**Quiet**\n(no lines in the last 15 minutes", body);
        Assert.DoesNotContain("hunter", body);
        Assert.DoesNotContain("```boom```", body);
        Assert.Equal(2, body.Split("```").Length - 1); // the one block this ticket has is opened and closed by us, never by a log line
        Assert.True(body.Length < 5000, body.Length.ToString());
    }

    [Fact]
    public async Task A_failing_log_source_never_stops_the_ticket()
    {
        var host = new FakeTicketHost();
        var logs = new FakeLogs { Failure = new HttpRequestException("Loki refused the log query: 500") };

        var outcomes = await Setup.Processor(host, routes: EvidenceRoutes, logs: logs).ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing())), default);

        Assert.Equal(AlertOutcome.Created, Assert.Single(outcomes).Action);
        Assert.Contains("the logs could not be read", Assert.Single(host.Tickets).Body);
    }

    [Fact]
    public void Evidence_queries_are_read_and_bounded()
    {
        var table = RouteTable.Parse("""{"routes":[{"match":{"service":"a"},"repository":"a/b","evidence":[{"title":"Errors","query":"{app=\"x\"}"}]}]}""");

        var route = table.Find(new Dictionary<string, string> { ["service"] = "a" })!;

        Assert.Equal(new EvidenceQuery("Errors", "{app=\"x\"}"), Assert.Single(route.Evidence!));
        Assert.Contains("at most 3", Assert.Throws<InvalidOperationException>(() => RouteTable.Parse(
            """{"routes":[{"match":{"service":"a"},"repository":"a/b","evidence":[{"title":"1","query":"q"},{"title":"2","query":"q"},{"title":"3","query":"q"},{"title":"4","query":"q"}]}]}""")).Message);
        Assert.Contains("\"title\"", Assert.Throws<InvalidOperationException>(() => RouteTable.Parse(
            """{"routes":[{"match":{"service":"a"},"repository":"a/b","evidence":[{"query":"q"}]}]}""")).Message);
    }

    private sealed class Canned(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }

    [Fact]
    public async Task Loki_lines_of_every_stream_come_back_oldest_first_and_cut_to_the_newest_few()
    {
        var handler = new Canned("""{"data":{"result":[{"values":[["3000","c"],["1000","a"]]},{"values":[["2000","b"],["4000","d"]]}]}}""");
        var client = new LokiClient(new HttpClient(handler) { BaseAddress = new Uri("http://loki:3100/") }, new ManualClock());

        var lines = await client.RecentLinesAsync("{app=\"x\"} |= \"err\"", TimeSpan.FromMinutes(15), 3, default);

        Assert.Equal(["b", "c", "d"], lines);
        Assert.Contains("query=%7Bapp%3D%22x%22%7D%20%7C%3D%20%22err%22", handler.Requested!.Query);
        Assert.Contains("limit=3", handler.Requested.Query);
    }

    [Fact]
    public async Task A_loki_answer_without_results_is_no_lines_and_a_refusal_is_reported_without_the_body()
    {
        var empty = new LokiClient(new HttpClient(new Canned("""{"data":{"result":[]}}""")) { BaseAddress = new Uri("http://loki:3100/") }, new ManualClock());
        var refused = new LokiClient(new HttpClient(new Canned("secret body", System.Net.HttpStatusCode.InternalServerError)) { BaseAddress = new Uri("http://loki:3100/") }, new ManualClock());

        Assert.Empty(await empty.RecentLinesAsync("q", TimeSpan.FromMinutes(1), 5, default));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => refused.RecentLinesAsync("q", TimeSpan.FromMinutes(1), 5, default));
        Assert.DoesNotContain("secret body", error.Message);
    }

    [Fact]
    public void The_loki_address_must_be_a_web_address()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ALERT_INTAKE_TOKEN"] = "0123456789abcdef", ["ALERT_INTAKE_LOKI_URL"] = "nonsense", ["ALERT_INTAKE_HOSTS"] = """[{"name":"forgejo","url":"http://forgejo:3000"}]""",
        }).Build();

        Assert.Contains("LOKI_URL", Assert.Throws<InvalidOperationException>(() => IntakeSettings.FromConfiguration(configuration)).Message);
    }
}
