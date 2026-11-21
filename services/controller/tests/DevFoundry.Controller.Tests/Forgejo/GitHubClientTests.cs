using System.Net;
using System.Text;
using System.Text.Json;

namespace DevFoundry.Controller.Tests;

/// <summary>The GitHub client against a fake API: what it asks for, and how it reads GitHub's answers in the factory's words.</summary>
public sealed class GitHubClientTests
{
    private sealed record Seen(HttpMethod Method, string Url, string? Body, string Accept, string? Authorization);

    private sealed class Handler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body, Dictionary<string, string>? Headers)> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                string.Join(",", request.Headers.Accept.Select(a => a.MediaType)), request.Headers.Authorization?.ToString()));
            var (status, body, headers) = answer(request);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers ?? [])
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }
            return response;
        }
    }

    private static (GitHubClient Client, Handler Handler) Client(Func<HttpRequestMessage, (HttpStatusCode, string, Dictionary<string, string>?)> answer, string token = "secret-token")
    {
        var handler = new Handler(answer);
        return (new GitHubClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, token), handler);
    }

    private static (HttpStatusCode, string, Dictionary<string, string>?) Ok(string json) => (HttpStatusCode.OK, json, null);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task A_comment_is_posted_with_githubs_headers_and_the_factorys_marker()
    {
        var (client, handler) = Client(_ => (HttpStatusCode.Created, "{}", null));

        await client.PostIssueCommentAsync("vacant", "factory-test", 7, "hello", CancellationToken.None);

        var seen = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "/repos/vacant/factory-test/issues/7/comments", "application/vnd.github+json", "Bearer secret-token"), (seen.Method, seen.Url, seen.Accept, seen.Authorization));
        Assert.Equal($"{EventRouter.BotMarker}\nhello", JsonDocument.Parse(seen.Body!).RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Without_a_token_nothing_is_asked_and_the_failure_is_for_good()
    {
        var (client, handler) = Client(_ => Ok("[]"), token: "");

        var error = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() => client.PostIssueCommentAsync("a", "b", 1, "x", CancellationToken.None));

        Assert.True(error.NonRetryable);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    public async Task Only_server_errors_and_rate_limits_are_worth_retrying_and_the_body_is_never_echoed(HttpStatusCode status, bool retryable)
    {
        var (client, _) = Client(_ => (status, "echoed-secret-body", null));

        var error = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() => client.PostIssueCommentAsync("a", "b", 1, "x", CancellationToken.None));

        Assert.Equal(!retryable, error.NonRetryable);
        Assert.StartsWith("GitHub rejected the comment", error.Message);
        Assert.DoesNotContain("echoed-secret-body", error.Message);
    }

    [Fact]
    public async Task A_forbidden_answer_with_no_requests_left_is_a_rate_limit_and_any_other_forbidden_is_not()
    {
        var limited = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() =>
            Client(_ => (HttpStatusCode.Forbidden, "{}", new() { ["X-RateLimit-Remaining"] = "0" })).Client.PostIssueCommentAsync("a", "b", 1, "x", CancellationToken.None));
        var forbidden = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() =>
            Client(_ => (HttpStatusCode.Forbidden, "{}", new() { ["X-RateLimit-Remaining"] = "42" })).Client.PostIssueCommentAsync("a", "b", 1, "x", CancellationToken.None));

        Assert.False(limited.NonRetryable);
        Assert.True(forbidden.NonRetryable);
    }

    [Fact]
    public async Task The_latest_comments_come_from_every_page_oldest_first_and_notices_are_not_repeated()
    {
        var page1 = Json(Enumerable.Range(1, 100).Select(i => new { body = $"c{i}" }));
        var page2 = Json(new[] { new { body = $"{TicketComments.NoticeMarker}\nan earlier notice" }, new { body = "last" } });
        var (client, handler) = Client(request => Ok(request.RequestUri!.Query.Contains("&page=1") ? page1 : page2));

        var recent = await client.RecentCommentBodiesAsync("vacant", "factory-test", 7, 3, CancellationToken.None);
        var posted = await client.PostNoticeOnceAsync("vacant", "factory-test", 7, "again", CancellationToken.None);

        Assert.Equal(["c100", $"{TicketComments.NoticeMarker}\nan earlier notice", "last"], recent);
        Assert.False(posted);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_ticket_is_opened_with_its_labels_and_the_number_and_address_come_back()
    {
        var (client, handler) = Client(_ => (HttpStatusCode.Created, Json(new { number = 9, html_url = "https://github.com/vacant/factory-test/issues/9" }), null));

        var issue = await client.CreateIssueAsync("vacant", "factory-test", "Title", "Body", ["incident"], CancellationToken.None);

        Assert.Equal(new IssueInfo(9, "https://github.com/vacant/factory-test/issues/9"), issue);
        var sent = JsonDocument.Parse(handler.Requests[0].Body!).RootElement;
        Assert.Equal(["incident"], sent.GetProperty("labels").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public async Task A_pull_request_is_read_with_its_branch_its_repositories_and_its_author()
    {
        var (client, _) = Client(_ => Ok(Json(new
        {
            title = "T", body = "B", state = "open", merged = false, user = new { login = "vacant" },
            head = new { @ref = "factory/issue-1", sha = "abc", repo = new { full_name = "vacant/factory-test" } },
            @base = new { @ref = "main", repo = new { full_name = "vacant/factory-test" } },
        })));

        var pull = await client.GetPullRequestAsync("vacant", "factory-test", 3, CancellationToken.None);

        Assert.Equal(new PullRequestDetails(3, "T", "B", "open", false, "factory/issue-1", "abc", "vacant/factory-test", "vacant/factory-test", "main", "vacant"), pull);
    }

    [Fact]
    public async Task A_pull_request_from_a_fork_names_the_fork_so_the_factory_can_refuse_it()
    {
        var (client, _) = Client(_ => Ok(Json(new
        {
            title = "T", state = "open", user = new { login = "stranger" },
            head = new { @ref = "factory/issue-1", sha = "abc", repo = new { full_name = "stranger/factory-test" } },
            @base = new { @ref = "main", repo = new { full_name = "vacant/factory-test" } },
        })));

        var pull = await client.GetPullRequestAsync("vacant", "factory-test", 3, CancellationToken.None);

        Assert.NotEqual(pull.HeadRepository, pull.BaseRepository);
    }

    [Fact]
    public async Task The_changed_files_are_listed_in_the_factorys_words_and_the_diff_is_asked_for_as_a_diff()
    {
        var files = Json(new object[]
        {
            new { filename = "a.py", status = "added", additions = 3, deletions = 0 },
            new { filename = "b.py", status = "removed", additions = 0, deletions = 5 },
            new { filename = "c.py", status = "renamed", additions = 1, deletions = 1, previous_filename = "old.py" },
            new { filename = "d.py", status = "changed", additions = 2, deletions = 2 },
        });
        var (client, handler) = Client(request => request.RequestUri!.AbsolutePath.EndsWith("/files") ? Ok(files) : Ok("diff --git a/a.py b/a.py\n+x\n"));

        var changes = await client.GetPullChangesAsync("vacant", "factory-test", 3, CancellationToken.None);

        Assert.Equal(["added", "deleted", "renamed", "modified"], changes.Files.Select(f => f.Status));
        Assert.Equal("old.py", changes.Files[2].PreviousPath);
        Assert.Equal(4, changes.TotalFiles);
        Assert.Equal("diff --git a/a.py b/a.py\n+x\n", changes.Diff);
        Assert.False(changes.DiffCut);
        Assert.Equal("application/vnd.github.diff", handler.Requests.Last().Accept);
    }

    [Fact]
    public async Task A_diff_github_will_not_give_comes_back_empty_and_cut_and_the_file_list_still_says_what_changed()
    {
        var (client, _) = Client(request => request.RequestUri!.AbsolutePath.EndsWith("/files")
            ? Ok(Json(new[] { new { filename = "a.py", status = "added", additions = 1, deletions = 0 } }))
            : (HttpStatusCode.NotAcceptable, "{\"message\":\"Sorry, the diff exceeded the maximum number of files (300).\"}", null));

        var changes = await client.GetPullChangesAsync("vacant", "factory-test", 3, CancellationToken.None);

        Assert.Equal(("", true, 1), (changes.Diff, changes.DiffCut, changes.Files.Count));
    }

    [Fact]
    public async Task Reviews_asking_for_changes_or_commenting_are_given_in_the_factorys_words_without_the_bots_own_or_approvals_or_old_ones()
    {
        string Answer(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/reviews") => Json(new object[]
            {
                new { id = 1, state = "APPROVED", user = new { login = "carol" }, body = "ok", submitted_at = "2026-11-20T10:00:00Z" },
                new { id = 2, state = "CHANGES_REQUESTED", user = new { login = "carol" }, body = "Rename it.", submitted_at = "2026-11-20T11:00:00Z" },
                new { id = 3, state = "COMMENTED", user = new { login = "dave" }, body = "", submitted_at = "2026-11-20T12:00:00Z" },
                new { id = 4, state = "COMMENTED", user = new { login = "vacant" }, body = "the bot's own", submitted_at = "2026-11-20T12:00:00Z" },
                new { id = 5, state = "CHANGES_REQUESTED", user = new { login = "erin" }, body = "old", submitted_at = "2026-11-01T12:00:00Z" },
                new { id = 6, state = "DISMISSED", user = new { login = "frank" }, body = "dismissed", submitted_at = "2026-11-20T12:00:00Z" },
            }),
            var p when p.EndsWith("/3/comments") => Json(new[] { new { id = 30, path = "a.py", line = (int?)null, original_line = 4, body = "why?", diff_hunk = "@@ -1 +1 @@", user = new { login = "dave" } } }),
            _ => "[]",
        };
        var (client, _) = Client(request => Ok(Answer(request)));

        var reviews = await client.GetUnaddressedReviewsAsync("vacant", "factory-test", 3, "vacant", DateTimeOffset.Parse("2026-11-10T00:00:00Z"), CancellationToken.None);

        Assert.Equal([2L, 3L], reviews.Select(r => r.Id));
        Assert.Equal(["REQUEST_CHANGES", "COMMENT"], reviews.Select(r => r.State));
        Assert.Equal("Rename it.", reviews[0].Body);
        var comment = Assert.Single(reviews[1].Comments);
        Assert.Equal(("a.py", 4, "why?", "dave"), (comment.Path, comment.Line, comment.Body, comment.Author));
    }

    [Fact]
    public async Task The_factorys_replies_are_counted_by_their_marker_with_the_time_of_the_last()
    {
        var (client, _) = Client(_ => Ok(Json(new object[]
        {
            new { body = $"{RevisionReply.Marker} one", created_at = "2026-11-20T10:00:00Z" },
            new { body = "a person", created_at = "2026-11-20T11:00:00Z" },
            new { body = $"{RevisionReply.Marker} two", created_at = "2026-11-20T12:00:00Z" },
        })));

        var history = await client.GetRevisionHistoryAsync("vacant", "factory-test", 3, CancellationToken.None);

        Assert.Equal(new RevisionHistory(2, DateTimeOffset.Parse("2026-11-20T12:00:00Z")), history);
    }

    [Fact]
    public async Task A_review_request_is_best_effort_and_only_a_server_error_is_an_error()
    {
        Assert.True(await Client(_ => (HttpStatusCode.Created, "{}", null)).Client.RequestReviewAsync("a", "b", 1, ["carol"], CancellationToken.None));
        Assert.False(await Client(_ => (HttpStatusCode.UnprocessableEntity, "{}", null)).Client.RequestReviewAsync("a", "b", 1, ["carol"], CancellationToken.None));
        Assert.False(await Client(_ => (HttpStatusCode.OK, "{}", null)).Client.RequestReviewAsync("a", "b", 1, [], CancellationToken.None));
        await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() => Client(_ => (HttpStatusCode.BadGateway, "{}", null)).Client.RequestReviewAsync("a", "b", 1, ["carol"], CancellationToken.None));
    }

    [Fact]
    public async Task The_factorys_open_pull_request_for_a_ticket_is_found_by_its_branch_and_the_newest_wins()
    {
        var (client, _) = Client(_ => Ok(Json(new object[]
        {
            new { number = 4, head = new { @ref = "factory/issue-7" } },
            new { number = 9, head = new { @ref = "factory/issue-7-2" } },
            new { number = 11, head = new { @ref = "factory/issue-70" } },
            new { number = 12, head = new { @ref = "feature/x" } },
        })));

        Assert.Equal(9, await client.FindFactoryPullRequestAsync("vacant", "factory-test", 7, CancellationToken.None));
        Assert.Null(await client.FindFactoryPullRequestAsync("vacant", "factory-test", 8, CancellationToken.None));
    }

    [Fact]
    public async Task For_the_report_a_pull_request_with_a_merge_date_was_merged_and_nothing_but_numbers_and_dates_are_kept()
    {
        var (client, _) = Client(_ => Ok(Json(new object[]
        {
            new { number = 1, state = "closed", title = "SECRET TITLE", body = "SECRET BODY", head = new { @ref = "factory/issue-1" }, user = new { login = "vacant" }, created_at = "2026-11-10T10:00:00Z", merged_at = "2026-11-11T10:00:00Z" },
            new { number = 2, state = "closed", head = new { @ref = "factory/issue-2" }, user = new { login = "vacant" }, created_at = "2026-11-10T10:00:00Z", merged_at = (string?)null },
            new { number = 3, state = "open", head = new { @ref = "factory/issue-3" }, user = new { login = "vacant" }, created_at = "2026-11-12T10:00:00Z", merged_at = (string?)null },
        })));

        var pulls = await client.ListAllPullRequestsAsync("vacant", "factory-test", CancellationToken.None);

        Assert.Equal([true, false, false], pulls.Select(p => p.Merged));
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T10:00:00Z"), pulls[0].MergedAt);
        Assert.DoesNotContain("SECRET", string.Join(" ", pulls.Select(p => p.ToString())));
    }

    [Fact]
    public async Task A_pull_request_that_already_exists_for_the_branch_is_returned_instead_of_failing()
    {
        var (client, handler) = Client(request => request.Method == HttpMethod.Post
            ? (HttpStatusCode.UnprocessableEntity, "{\"message\":\"Validation Failed\"}", null)
            : Ok(Json(new[] { new { number = 5, html_url = "https://github.com/vacant/factory-test/pull/5" } })));

        var pull = await client.CreatePullRequestAsync("vacant", "factory-test", "factory/issue-1", "main", "T", "B", CancellationToken.None);

        Assert.Equal(new PullRequestInfo(5, "https://github.com/vacant/factory-test/pull/5"), pull);
        Assert.Contains("head=vacant%3Afactory%2Fissue-1", handler.Requests.Last().Url);
    }

    [Fact]
    public async Task A_new_pull_request_comes_back_with_its_number_and_address()
    {
        var (client, handler) = Client(_ => (HttpStatusCode.Created, Json(new { number = 6, html_url = "https://github.com/vacant/factory-test/pull/6" }), null));

        var pull = await client.CreatePullRequestAsync("vacant", "factory-test", "factory/issue-2", "main", "T", "B", CancellationToken.None);

        Assert.Equal(new PullRequestInfo(6, "https://github.com/vacant/factory-test/pull/6"), pull);
        var sent = JsonDocument.Parse(handler.Requests[0].Body!).RootElement;
        Assert.Equal(("factory/issue-2", "main"), (sent.GetProperty("head").GetString(), sent.GetProperty("base").GetString()));
    }

    // ---- the janitor's five

    [Fact]
    public async Task Only_repositories_the_token_can_push_to_are_listed()
    {
        var (client, handler) = Client(_ => Ok(Json(new object[]
        {
            new { full_name = "vacant/factory-test", permissions = new { push = true } },
            new { full_name = "other/readonly", permissions = new { push = false } },
            new { full_name = "other/none" },
        })));

        var repositories = await client.ListWritableRepositoriesAsync(CancellationToken.None);

        Assert.Equal([("vacant", "factory-test")], repositories);
        Assert.StartsWith("/user/repos?affiliation=", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Only_the_factorys_work_in_progress_branches_are_listed_each_with_the_date_of_its_last_commit()
    {
        var (client, handler) = Client(request => request.RequestUri!.AbsolutePath.EndsWith("/branches")
            ? Ok(Json(new object[] { new { name = "main" }, new { name = "factory/issue-4-wip" }, new { name = "feature/x" } }))
            : Ok(Json(new { commit = new { commit = new { committer = new { date = "2026-08-31T21:00:00Z" } } } })));

        var branches = await client.ListBranchesAsync("vacant", "factory-test", CancellationToken.None);

        Assert.Equal([new BranchInfo("factory/issue-4-wip", DateTimeOffset.Parse("2026-08-31T21:00:00Z"))], branches);
        Assert.Contains(handler.Requests, request => request.Url == "/repos/vacant/factory-test/branches/factory%2Fissue-4-wip");
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"state":"closed"}""", true)]
    [InlineData(HttpStatusCode.OK, """{"state":"open"}""", false)]
    [InlineData(HttpStatusCode.NotFound, "{}", false)]
    public async Task A_ticket_counts_as_closed_only_when_github_says_so(HttpStatusCode status, string body, bool closed) =>
        Assert.Equal(closed, await Client(_ => (status, body, null)).Client.IssueIsClosedAsync("vacant", "factory-test", 4, CancellationToken.None));

    [Fact]
    public async Task The_heads_of_open_pull_requests_are_collected()
    {
        var (client, _) = Client(_ => Ok(Json(new object[] { new { head = new { @ref = "factory/issue-7" } }, new { head = new { @ref = "factory/issue-9-wip" } } })));

        var heads = await client.OpenPullRequestBranchesAsync("vacant", "factory-test", CancellationToken.None);

        Assert.Equal(["factory/issue-7", "factory/issue-9-wip"], heads.Order());
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_branch_is_deleted_by_its_reference_and_one_that_is_already_gone_is_fine(HttpStatusCode status)
    {
        var (client, handler) = Client(_ => (status, "{}", null));

        await client.DeleteBranchAsync("vacant", "factory-test", "factory/issue-4-wip", CancellationToken.None);

        Assert.Equal((HttpMethod.Delete, "/repos/vacant/factory-test/git/refs/heads/factory/issue-4-wip"), (handler.Requests[0].Method, handler.Requests[0].Url));
    }

    // ---- where the host is, and how git signs in to it

    [Fact]
    public void The_api_is_where_the_kind_of_host_puts_it()
    {
        Assert.Equal("https://api.github.com", new HostSettings("github", "github", "https://github.com", "t", "b").ApiBase);
        Assert.Equal("https://api.github.com", new HostSettings("github", "github", "https://github.com/", "t", "b").ApiBase);
        Assert.Equal("https://ghe.example.com/api/v3", new HostSettings("work", "github", "https://ghe.example.com", "t", "b").ApiBase);
        Assert.Equal("https://codeberg.org", new HostSettings("codeberg", "forgejo", "https://codeberg.org/", "t", "b").ApiBase);
    }

    [Fact]
    public void Git_signs_in_to_github_with_basic_and_to_forgejo_with_a_token_header()
    {
        var github = new HostSettings("github", "github", "https://github.com", "ghp_secret", "b").GitAuthorization;
        var forgejo = new HostSettings("forgejo", "forgejo", "http://forgejo:3000", "fj_secret", "b").GitAuthorization;

        Assert.Equal("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:ghp_secret")), github);
        Assert.Equal("Authorization: token fj_secret", forgejo);
        Assert.Equal("", new HostSettings("github", "github", "https://github.com", "", "b").GitAuthorization);
    }
}
