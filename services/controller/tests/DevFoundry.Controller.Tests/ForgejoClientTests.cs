using System.Net;
using System.Text.Json;
using Temporalio.Exceptions;

namespace DevFoundry.Controller.Tests;

public sealed class ForgejoClientTests
{
    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }

    private static (ForgejoClient Client, RecordingHandler Handler) Create(
        HttpStatusCode status = HttpStatusCode.Created, string token = "secret-token")
    {
        var handler = new RecordingHandler(status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://forgejo:3000/") };
        return (new ForgejoClient(http, new WorkerSettings { ForgejoToken = token }), handler);
    }

    [Fact]
    public async Task Comment_is_posted_with_the_token_and_the_bot_marker()
    {
        var (client, handler) = Create();

        await client.PostIssueCommentAsync("acme", "shop", 7, "Hello", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("http://forgejo:3000/api/v1/repos/acme/shop/issues/7/comments", handler.Request.RequestUri!.ToString());
        Assert.Equal("token secret-token", handler.Request.Headers.Authorization!.ToString());
        var body = JsonDocument.Parse(handler.Body!).RootElement.GetProperty("body").GetString();
        Assert.StartsWith(EventRouter.BotMarker, body);
        Assert.EndsWith("Hello", body);
    }

    [Fact]
    public async Task Missing_token_fails_without_retrying_or_calling_forgejo()
    {
        var (client, handler) = Create(token: "");
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => client.PostIssueCommentAsync("acme", "shop", 7, "Hello", CancellationToken.None));
        Assert.True(error.NonRetryable);
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    public async Task Failures_are_retryable_only_when_they_may_pass(HttpStatusCode status, bool nonRetryable)
    {
        var (client, _) = Create(status);
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => client.PostIssueCommentAsync("acme", "shop", 7, "Hello", CancellationToken.None));
        Assert.Equal(nonRetryable, error.NonRetryable);
        Assert.DoesNotContain("secret-token", error.Message);
    }
}
