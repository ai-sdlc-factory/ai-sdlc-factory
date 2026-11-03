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

    private sealed class Conversation(params string[] existing) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(existing.Select(body => new { body }))) }
                : new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    private static ForgejoClient WithComments(Conversation conversation) =>
        new(new HttpClient(conversation) { BaseAddress = new Uri("http://forgejo:3000/") }, new WorkerSettings { ForgejoToken = "secret-token" });

    [Fact]
    public async Task A_notice_is_posted_when_none_of_the_latest_comments_is_one()
    {
        var conversation = new Conversation("first", "second");

        var posted = await WithComments(conversation).PostNoticeOnceAsync("acme", "shop", 7, "Nobody is listening.", CancellationToken.None);

        Assert.True(posted);
        var post = Assert.Single(conversation.Calls, call => call.Method == HttpMethod.Post);
        var body = JsonDocument.Parse(post.Body!).RootElement.GetProperty("body").GetString()!;
        Assert.StartsWith(EventRouter.BotMarker, body);
        Assert.Contains(TicketComments.NoticeMarker, body);
        Assert.EndsWith("Nobody is listening.", body);
    }

    [Fact]
    public async Task A_notice_is_not_repeated_while_it_is_among_the_latest_comments()
    {
        var conversation = new Conversation(["old", $"{EventRouter.BotMarker}\n{TicketComments.NoticeMarker}\nNobody is listening.", .. Enumerable.Range(1, 8).Select(i => $"comment {i}")]);

        var posted = await WithComments(conversation).PostNoticeOnceAsync("acme", "shop", 7, "Nobody is listening.", CancellationToken.None);

        Assert.False(posted);
        Assert.DoesNotContain(conversation.Calls, call => call.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task An_old_notice_far_back_in_the_conversation_does_not_silence_a_new_one()
    {
        var conversation = new Conversation([TicketComments.NoticeMarker, .. Enumerable.Range(1, 10).Select(i => $"comment {i}")]);

        var posted = await WithComments(conversation).PostNoticeOnceAsync("acme", "shop", 7, "Nobody is listening.", CancellationToken.None);

        Assert.True(posted);
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

public sealed class NoticeTextTests
{
    [Fact]
    public void The_unused_comment_notice_agrees_with_the_number()
    {
        Assert.Contains("1 comment arrived", TicketComments.CommentsNotUsed(1));
        Assert.Contains("it was not used", TicketComments.CommentsNotUsed(1));
        Assert.Contains("3 comments arrived", TicketComments.CommentsNotUsed(3));
        Assert.Contains("they were not used", TicketComments.CommentsNotUsed(3));
    }

    [Fact]
    public void Every_notice_says_how_to_get_a_new_run()
    {
        Assert.Contains("close and reopen", TicketComments.CommentTooLate);
        Assert.Contains("close and reopen", TicketComments.CommentsNotUsed(2));
        Assert.Contains("close and reopen", TicketComments.NoRunActive);
    }
}
