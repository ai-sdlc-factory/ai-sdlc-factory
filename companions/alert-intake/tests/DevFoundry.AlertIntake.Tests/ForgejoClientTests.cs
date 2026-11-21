using System.Net;
using System.Text;
using System.Text.Json;

namespace DevFoundry.AlertIntake.Tests;

public sealed class ForgejoClientTests
{
    /// <summary>A stand-in for Forgejo's API: answers by method and path, and keeps what it was sent.</summary>
    private sealed class FakeForgejoApi : HttpMessageHandler
    {
        public List<(HttpMethod Method, string PathAndQuery, string Body)> Requests { get; } = [];
        public Dictionary<string, (HttpStatusCode Status, string Json)> Answers { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            var key = $"{request.Method} {request.RequestUri.PathAndQuery}";
            if (!Answers.TryGetValue(key, out var answer))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no answer scripted for {key}") };
            }
            return new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Json, Encoding.UTF8, "application/json") };
        }
    }

    private static ForgejoClient Client(FakeForgejoApi api) =>
        new(new HttpClient(api) { BaseAddress = new Uri("http://forgejo:3000/") });

    private static string Issue(int number, string body) => JsonSerializer.Serialize(new { number, body, html_url = $"http://forgejo/valters/weather/issues/{number}" });

    [Fact]
    public async Task An_open_ticket_with_the_alerts_marker_is_found_on_a_later_page()
    {
        var api = new FakeForgejoApi();
        var firstPage = "[" + string.Join(",", Enumerable.Range(1, 50).Select(i => Issue(i, "an ordinary ticket"))) + "]";
        api.Answers["GET /api/v1/repos/valters/weather/issues?state=open&type=issues&limit=50&page=1"] = (HttpStatusCode.OK, firstPage);
        api.Answers["GET /api/v1/repos/valters/weather/issues?state=open&type=issues&limit=50&page=2"] =
            (HttpStatusCode.OK, "[" + Issue(77, "text\n" + TicketText.Marker("abc123")) + "]");

        var found = await Client(api).FindOpenTicketAsync("valters", "weather", "abc123", default);

        Assert.Equal(77, found);
    }

    [Fact]
    public async Task No_ticket_with_the_marker_is_null_and_a_similar_fingerprint_is_not_a_match()
    {
        var api = new FakeForgejoApi();
        api.Answers["GET /api/v1/repos/valters/weather/issues?state=open&type=issues&limit=50&page=1"] =
            (HttpStatusCode.OK, "[" + Issue(5, TicketText.Marker("abc1234")) + "]");
        api.Answers["GET /api/v1/repos/valters/weather/issues?state=open&type=issues&limit=50&page=2"] = (HttpStatusCode.OK, "[]");

        Assert.Null(await Client(api).FindOpenTicketAsync("valters", "weather", "abc123", default));
    }

    [Fact]
    public async Task A_ticket_gets_the_ids_of_its_labels_and_a_missing_label_is_created_first()
    {
        var api = new FakeForgejoApi();
        api.Answers["GET /api/v1/repos/valters/weather/labels?limit=100"] = (HttpStatusCode.OK, """[{"id":4,"name":"Bug"}]""");
        api.Answers["POST /api/v1/repos/valters/weather/labels"] = (HttpStatusCode.Created, """{"id":9,"name":"incident"}""");
        api.Answers["POST /api/v1/repos/valters/weather/issues"] = (HttpStatusCode.Created, Issue(31, ""));

        var ticket = await Client(api).CreateTicketAsync("valters", "weather", "[alert] X", "body", ["bug", "incident"], default);

        Assert.Equal(31, ticket.Number);
        Assert.Equal("http://forgejo/valters/weather/issues/31", ticket.Url);
        var created = api.Requests.Single(r => r.Method == HttpMethod.Post && r.PathAndQuery.EndsWith("/issues"));
        using var document = JsonDocument.Parse(created.Body);
        Assert.Equal("[alert] X", document.RootElement.GetProperty("title").GetString());
        Assert.Equal([4L, 9L], document.RootElement.GetProperty("labels").EnumerateArray().Select(id => id.GetInt64()));
        var label = api.Requests.Single(r => r.PathAndQuery.EndsWith("/labels") && r.Method == HttpMethod.Post);
        Assert.Contains("\"name\":\"incident\"", label.Body);
    }

    [Fact]
    public async Task A_comment_is_posted_to_the_ticket()
    {
        var api = new FakeForgejoApi();
        api.Answers["POST /api/v1/repos/valters/weather/issues/31/comments"] = (HttpStatusCode.Created, "{}");

        await Client(api).CommentAsync("valters", "weather", 31, "resolved", default);

        Assert.Contains("\"body\":\"resolved\"", Assert.Single(api.Requests).Body);
    }

    [Fact]
    public async Task A_refusal_names_the_status_and_never_echoes_the_answers_body()
    {
        var api = new FakeForgejoApi();
        api.Answers["POST /api/v1/repos/valters/weather/issues/31/comments"] = (HttpStatusCode.Forbidden, "secret detail from Forgejo");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(api).CommentAsync("valters", "weather", 31, "x", default));

        Assert.Contains("403", error.Message);
        Assert.DoesNotContain("secret detail", error.Message);
    }

    [Fact]
    public async Task Owner_and_repository_names_cannot_change_the_path()
    {
        var api = new FakeForgejoApi();

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(api).CommentAsync("a/../b", "r?x=1", 1, "x", default));

        Assert.DoesNotContain("/../", api.Requests.Single().PathAndQuery);
        Assert.DoesNotContain("?x=1", api.Requests.Single().PathAndQuery);
    }
}
