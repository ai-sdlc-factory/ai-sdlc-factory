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

public sealed class CreateIssueTests
{
    private sealed class Handler(string labelsJson) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(labelsJson) }
                : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"number":12,"html_url":"http://forgejo/acme/shop/issues/12"}""") };
        }
    }

    private static ForgejoClient Client(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://forgejo:3000/") }, new WorkerSettings { ForgejoToken = "secret-token" });

    [Fact]
    public async Task A_ticket_is_opened_with_the_ids_of_the_labels_asked_for_by_name()
    {
        var handler = new Handler("""[{"id":4,"name":"feature"},{"id":5,"name":"Bug"}]""");

        var issue = await Client(handler).CreateIssueAsync("acme", "shop", "Fix it", "Body text", ["bug"], CancellationToken.None);

        Assert.Equal(new IssueInfo(12, "http://forgejo/acme/shop/issues/12"), issue);
        var post = Assert.Single(handler.Calls, call => call.Method == HttpMethod.Post);
        Assert.Equal("/api/v1/repos/acme/shop/issues", post.Path);
        var body = JsonDocument.Parse(post.Body!).RootElement;
        Assert.Equal("Fix it", body.GetProperty("title").GetString());
        Assert.Equal("Body text", body.GetProperty("body").GetString());
        Assert.Equal([5], body.GetProperty("labels").EnumerateArray().Select(id => id.GetInt32()));
    }

    [Fact]
    public async Task A_missing_label_fails_for_good_and_opens_nothing()
    {
        var handler = new Handler("""[{"id":4,"name":"feature"}]""");

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => Client(handler).CreateIssueAsync("acme", "shop", "Fix it", "Body", ["bug"], CancellationToken.None));

        Assert.True(error.NonRetryable);
        Assert.Contains("has no \"bug\" label", error.Message);
        Assert.DoesNotContain(handler.Calls, call => call.Method == HttpMethod.Post);
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

public sealed class RevisionClientTests
{
    /// <summary>Answers each request by its path, so a test can describe a small Forgejo.</summary>
    private sealed class Forgejo(Dictionary<string, string> responses) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(responses.TryGetValue(request.RequestUri.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static ForgejoClient Client(Forgejo handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://forgejo:3000/") }, new WorkerSettings { ForgejoToken = "secret-token" });

    private const string Pulls = "/api/v1/repos/acme/shop/pulls/7";

    [Fact]
    public async Task A_pull_request_is_read_with_its_branch_sides_state_and_author()
    {
        var forgejo = new Forgejo(new()
        {
            [Pulls] = """{"title":"Add goodbye","body":"Closes #3","state":"open","merged":false,"user":{"login":"factory-bot"}, "head":{"ref":"factory/issue-3","sha":"abc123","repo":{"full_name":"acme/shop"}}, "base":{"ref":"main","repo":{"full_name":"acme/shop"}}}""",
        });

        var pull = await Client(forgejo).GetPullRequestAsync("acme", "shop", 7, CancellationToken.None);

        Assert.Equal(new PullRequestDetails(7, "Add goodbye", "Closes #3", "open", false, "factory/issue-3", "abc123", "acme/shop", "acme/shop", "main", "factory-bot"), pull);
    }

    [Fact]
    public async Task A_pull_request_with_missing_parts_is_read_as_empty_text_not_an_error()
    {
        var forgejo = new Forgejo(new() { [Pulls] = """{"state":"closed","merged":true}""" });

        var pull = await Client(forgejo).GetPullRequestAsync("acme", "shop", 7, CancellationToken.None);

        Assert.True(pull.Merged);
        Assert.Equal("", pull.HeadRef);
        Assert.Equal("", pull.Author);
    }

    [Fact]
    public async Task A_pull_request_that_cannot_be_read_fails_with_a_message()
    {
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() => Client(new Forgejo([])).GetPullRequestAsync("acme", "shop", 7, CancellationToken.None));

        Assert.Contains("pull request", error.Message);
        Assert.True(error.NonRetryable);
    }

    [Fact]
    public async Task Every_review_waiting_is_read_with_its_comments_and_code_and_resolved_ones_are_skipped()
    {
        var forgejo = new Forgejo(new()
        {
            [$"{Pulls}/reviews"] = """[ {"id":3,"state":"APPROVED","body":"fine","user":{"login":"carol"}}, {"id":8,"state":"COMMENT","body":"factory said","user":{"login":"factory-bot"}}, {"id":9,"state":"REQUEST_CHANGES","body":"dismissed one","dismissed":true,"user":{"login":"dave"}}, {"id":7,"state":"REQUEST_CHANGES","body":"Please rename these.","user":{"login":"bob"}}, {"id":5,"state":"COMMENT","body":"","user":{"login":"bob"}}, {"id":4,"state":"COMMENT","body":"old","submitted_at":"2026-01-01T00:00:00Z","user":{"login":"bob"}}]""",
            [$"{Pulls}/reviews/7/comments"] = """[ {"id":21,"body":"Rename this.","path":"hello.py","line":4,"diff_hunk":"@@ -1 +1 @@","commit_id":"c1","user":{"login":"bob"}}, {"id":22,"body":"On an old diff.","path":"test_hello.py","line":0,"original_position":9,"commit_id":"c2","user":{"login":"bob"}}, {"id":23,"body":"Done already.","path":"x.py","line":1,"resolver":{"login":"bob"},"user":{"login":"bob"}}]""",
            [$"{Pulls}/reviews/5/comments"] = """[ {"id":31,"body":"Single note.","path":"a.py","line":2,"commit_id":"c3","user":{"login":"bob"}} ]""",
        });

        var reviews = await Client(forgejo).GetUnaddressedReviewsAsync("acme", "shop", 7, "factory-bot", DateTimeOffset.Parse("2026-06-01T00:00:00Z"), CancellationToken.None);

        Assert.Equal([5, 7], reviews.Select(r => r.Id));
        var seven = reviews[1];
        Assert.Equal(("bob", "REQUEST_CHANGES", "Please rename these."), (seven.Reviewer, seven.State, seven.Body));
        Assert.Equal(
            [("hello.py", 4, "Rename this.", "c1", false), ("test_hello.py", 9, "On an old diff.", "c2", true)],
            seven.Comments.Select(c => (c.Path, c.Line, c.Body, c.CommitId, c.OldSide)));
        Assert.Equal("@@ -1 +1 @@", seven.Comments[0].DiffHunk);
    }

    [Fact]
    public async Task A_pull_request_with_only_approvals_or_the_bots_own_reviews_has_nothing_to_act_on()
    {
        var forgejo = new Forgejo(new()
        {
            [$"{Pulls}/reviews"] = """[{"id":3,"state":"APPROVED","body":"fine","user":{"login":"carol"}},{"id":4,"state":"COMMENT","body":"mine","user":{"login":"factory-bot"}}]""",
        });

        Assert.Empty(await Client(forgejo).GetUnaddressedReviewsAsync("acme", "shop", 7, "factory-bot", null, CancellationToken.None));
    }

    [Fact]
    public async Task A_review_whose_comments_cannot_be_read_still_gives_its_text()
    {
        var forgejo = new Forgejo(new() { [$"{Pulls}/reviews"] = """[{"id":7,"state":"REQUEST_CHANGES","body":"Please rename.","user":{"login":"bob"}}]""" });

        var review = Assert.Single(await Client(forgejo).GetUnaddressedReviewsAsync("acme", "shop", 7, "factory-bot", null, CancellationToken.None));

        Assert.Equal("Please rename.", review.Body);
        Assert.Empty(review.Comments);
    }

    [Fact]
    public async Task Revisions_are_counted_and_the_last_reply_time_is_found_from_the_marker()
    {
        var forgejo = new Forgejo(new()
        {
            ["/api/v1/repos/acme/shop/issues/7/comments"] = JsonSerializer.Serialize(new[]
            {
                new { body = "a person's comment", created_at = "2026-05-01T00:00:00Z" },
                new { body = $"{EventRouter.BotMarker}\n{RevisionReply.Marker}\n**Revision pushed**", created_at = "2026-05-02T00:00:00Z" },
                new { body = $"{EventRouter.BotMarker}\nA normal factory comment", created_at = "2026-05-09T00:00:00Z" },
                new { body = $"{EventRouter.BotMarker}\n{RevisionReply.Marker}\n**Revision: no code change**", created_at = "2026-05-03T00:00:00Z" },
            }),
        });

        var history = await Client(forgejo).GetRevisionHistoryAsync("acme", "shop", 7, CancellationToken.None);

        Assert.Equal(2, history.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-05-03T00:00:00Z"), history.LastAt);
    }
}
