using System.Net;
using System.Text;
using System.Text.Json;

namespace DevFoundry.AlertIntake.Tests;

public sealed class GitHubClientTests
{
    /// <summary>A stand-in for GitHub's API: answers by method and path, and keeps what it was sent.</summary>
    private sealed class FakeGitHubApi : HttpMessageHandler
    {
        public List<(HttpMethod Method, string PathAndQuery, string Body, System.Net.Http.Headers.HttpRequestHeaders Headers)> Requests { get; } = [];
        public Dictionary<string, (HttpStatusCode Status, string Json)> Answers { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Headers));
            var key = $"{request.Method} {request.RequestUri.PathAndQuery}";
            if (!Answers.TryGetValue(key, out var answer))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no answer scripted for {key}") };
            }
            return new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Json, Encoding.UTF8, "application/json") };
        }
    }

    private static GitHubClient Client(FakeGitHubApi api, string token = "")
    {
        var http = new HttpClient(api) { BaseAddress = new Uri("https://api.github.com/") };
        GitHubClient.Prepare(http, token);
        return new GitHubClient(http);
    }

    private static string Issue(int number, string body, bool pull = false, string created = "2026-11-21T10:00:00Z") =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["number"] = number, ["body"] = body, ["created_at"] = created, ["html_url"] = $"https://github.com/acme/shop/issues/{number}", ["pull_request"] = pull ? new { } : null }
            .Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value));

    [Theory]
    [InlineData("https://github.com", "https://api.github.com")]
    [InlineData("https://github.com/", "https://api.github.com")]
    [InlineData("https://git.example.com", "https://git.example.com/api/v3")]
    public void The_api_of_github_com_and_of_an_enterprise_server_are_told_apart(string url, string api) =>
        Assert.Equal(api, GitHubClient.ApiBase(url));

    [Fact]
    public async Task Every_request_carries_the_headers_github_asks_for_and_the_token_as_a_bearer()
    {
        var api = new FakeGitHubApi();
        api.Answers["GET /repos/acme/shop/issues?state=open&per_page=50&page=1"] = (HttpStatusCode.OK, "[]");

        await Client(api, "secret-token").FindOpenTicketAsync("acme", "shop", "abc", default);

        var headers = api.Requests[0].Headers;
        Assert.Equal("Bearer", headers.Authorization!.Scheme);
        Assert.Equal("secret-token", headers.Authorization.Parameter);
        Assert.Contains("application/vnd.github+json", headers.Accept.Select(accept => accept.MediaType));
        Assert.Contains("2022-11-28", headers.GetValues("X-GitHub-Api-Version"));
        Assert.NotEmpty(headers.UserAgent);
    }

    [Fact]
    public async Task An_open_ticket_with_the_alerts_marker_is_found_and_a_pull_request_with_it_is_not()
    {
        var api = new FakeGitHubApi();
        api.Answers["GET /repos/acme/shop/issues?state=open&per_page=50&page=1"] =
            (HttpStatusCode.OK, "[" + Issue(3, TicketText.Marker("abc123"), pull: true) + "," + Issue(8, "text\n" + TicketText.Marker("abc123")) + "]");

        Assert.Equal(8, await Client(api).FindOpenTicketAsync("acme", "shop", "abc123", default));
    }

    [Fact]
    public async Task No_ticket_with_the_marker_is_null_and_a_similar_fingerprint_is_not_a_match()
    {
        var api = new FakeGitHubApi();
        api.Answers["GET /repos/acme/shop/issues?state=open&per_page=50&page=1"] = (HttpStatusCode.OK, "[" + Issue(5, TicketText.Marker("abc1234")) + "]");
        api.Answers["GET /repos/acme/shop/issues?state=open&per_page=50&page=2"] = (HttpStatusCode.OK, "[]");

        Assert.Null(await Client(api).FindOpenTicketAsync("acme", "shop", "abc123", default));
    }

    [Fact]
    public async Task A_recent_ticket_must_carry_the_route_marker_and_be_new_enough()
    {
        var api = new FakeGitHubApi();
        const string marker = "<!-- dev-foundry:alert-group route=abc -->";
        api.Answers["GET /repos/acme/shop/issues?state=open&per_page=50&page=1"] =
            (HttpStatusCode.OK, "[" + Issue(1, marker, created: "2026-11-21T08:00:00Z") + "," + Issue(2, marker, created: "2026-11-21T11:50:00Z") + "]");

        var found = await Client(api).FindRecentTicketAsync("acme", "shop", marker, DateTimeOffset.Parse("2026-11-21T11:45:00Z"), default);

        Assert.Equal(2, found);
    }

    [Fact]
    public async Task A_ticket_is_created_with_its_label_names_and_its_number_and_address_come_back()
    {
        var api = new FakeGitHubApi();
        api.Answers["POST /repos/acme/shop/issues"] = (HttpStatusCode.Created, Issue(12, "b"));

        var ticket = await Client(api).CreateTicketAsync("acme", "shop", "[alert] X", "body", ["incident", "prod"], default);

        Assert.Equal(new IssueRef(12, "https://github.com/acme/shop/issues/12"), ticket);
        using var sent = JsonDocument.Parse(api.Requests[0].Body);
        Assert.Equal("[alert] X", sent.RootElement.GetProperty("title").GetString());
        Assert.Equal(["incident", "prod"], sent.RootElement.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
    }

    [Fact]
    public async Task A_marker_is_added_to_the_ticket_text_once()
    {
        var api = new FakeGitHubApi();
        api.Answers["GET /repos/acme/shop/issues/4"] = (HttpStatusCode.OK, Issue(4, "original text"));
        api.Answers["PATCH /repos/acme/shop/issues/4"] = (HttpStatusCode.OK, Issue(4, "x"));

        await Client(api).AddMarkerAsync("acme", "shop", 4, "<!-- m -->", default);

        using var sent = JsonDocument.Parse(api.Requests[1].Body);
        Assert.Equal("original text\n<!-- m -->", sent.RootElement.GetProperty("body").GetString());

        var again = new FakeGitHubApi();
        again.Answers["GET /repos/acme/shop/issues/4"] = (HttpStatusCode.OK, Issue(4, "original text\n<!-- m -->"));
        await Client(again).AddMarkerAsync("acme", "shop", 4, "<!-- m -->", default);
        Assert.Single(again.Requests);
    }

    [Fact]
    public async Task A_comment_is_posted_to_the_ticket()
    {
        var api = new FakeGitHubApi();
        api.Answers["POST /repos/acme/shop/issues/4/comments"] = (HttpStatusCode.Created, "{}");

        await Client(api).CommentAsync("acme", "shop", 4, "resolved", default);

        using var sent = JsonDocument.Parse(api.Requests[0].Body);
        Assert.Equal("resolved", sent.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_refusal_names_the_status_and_never_echoes_what_was_sent()
    {
        var api = new FakeGitHubApi();
        api.Answers["POST /repos/acme/shop/issues"] = (HttpStatusCode.Forbidden, "{\"message\":\"secret-body-echo\"}");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(api).CreateTicketAsync("acme", "shop", "t", "secret-body-echo", [], default));

        Assert.Contains("403", error.Message);
        Assert.DoesNotContain("secret-body-echo", error.Message);
    }

    [Fact]
    public async Task Owner_and_repository_names_cannot_change_the_path()
    {
        var api = new FakeGitHubApi();

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(api).CommentAsync("../x", "a/b", 1, "c", default));

        Assert.DoesNotContain(api.Requests, request => request.PathAndQuery.Contains("/x/") || request.PathAndQuery.Contains("a/b"));
    }
}
