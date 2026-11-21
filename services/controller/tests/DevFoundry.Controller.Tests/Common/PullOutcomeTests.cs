using System.Net;

namespace DevFoundry.Controller.Tests;

public sealed class PullOutcomeTests : IDisposable
{
    private static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-11-09T00:00:00Z");
    private static readonly DateTimeOffset To = DateTimeOffset.Parse("2026-11-16T00:00:00Z");
    private readonly string root = Path.Combine(Path.GetTempPath(), "po-" + Guid.NewGuid().ToString("N"));

    public PullOutcomeTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static PullSummary Pull(int number, string head = "factory/issue-1", string state = "open", bool merged = false, string author = "factory-bot", string created = "2026-11-10T10:00:00Z", string? mergedAt = null) =>
        new(number, state, merged, head, author, DateTimeOffset.Parse(created), mergedAt is null ? null : DateTimeOffset.Parse(mergedAt));

    private static readonly PullScope Scope = new(["acme/shop"], [new LoggedPullKind("acme/shop", 1, "feature"), new LoggedPullKind("acme/shop", 2, "bug"), new LoggedPullKind("acme/other", 1, "docs")], [new LoggedRevisions("acme/shop", 11, 2)]);

    [Fact]
    public void Only_pull_requests_the_bot_opened_from_a_factory_branch_in_the_week_count()
    {
        var pulls = new[]
        {
            Pull(10, "factory/issue-1"),
            Pull(11, "factory/issue-2-2", "closed", merged: true, mergedAt: "2026-11-12T10:00:00Z"),
            Pull(12, "factory/issue-1", author: "alice"),            // a person's
            Pull(13, "feature/login"),                                // not a factory branch
            Pull(14, "factory/issue-1", created: "2026-11-02T10:00:00Z"), // an earlier week
            Pull(15, "factory/issue-1", created: "2026-11-16T00:00:00Z"), // the next week starts at the end, not included
            Pull(16, "factory/issue-9-wip", "closed"),                // a work-in-progress branch is no pull request of the factory's
            Pull(17, "factory/issue-7", "closed"),                    // a ticket the log does not know
        };

        var outcomes = PullOutcomes.Build("acme/shop", pulls, Scope, "Factory-Bot", From, To);

        Assert.Equal([10, 11, 17], outcomes.Select(o => o.Number));
        Assert.Equal(["feature", "bug", PullOutcomes.UnknownKind], outcomes.Select(o => o.Kind));
        Assert.Equal([PullStates.Open, PullStates.Merged, PullStates.ClosedUnmerged], outcomes.Select(o => o.State));
    }

    [Fact]
    public void A_merged_pull_request_has_its_revisions_and_its_days_to_merge_and_the_others_have_none()
    {
        var pulls = new[] { Pull(11, "factory/issue-2", "closed", merged: true, created: "2026-11-10T00:00:00Z", mergedAt: "2026-11-11T12:00:00Z"), Pull(10, "factory/issue-1", "closed") };

        var outcomes = PullOutcomes.Build("acme/shop", pulls, Scope, "factory-bot", From, To);

        var merged = outcomes.Single(o => o.Number == 11);
        Assert.Equal(2, merged.Revisions);
        Assert.Equal(1.5, merged.DaysToMerge);
        Assert.Null(outcomes.Single(o => o.Number == 10).DaysToMerge);
    }

    [Fact]
    public async Task The_usage_log_names_the_repositories_the_kinds_and_the_revisions_that_were_pushed()
    {
        var workspace = new GitWorkspace(new WorkerSettings { Hosts = TestHosts.Forgejo(), WorkspaceRoot = root });
        UsageLogEntry Entry(string run, string kind, string status, string repo, int number) =>
            new($"{kind}-{run}", run, kind, repo, number, status, new TokenUsage(1000, 0, 0, 0, 0, 1000, 0, 1));
        await workspace.LogUsageAsync(Entry("a", "feature", "pr_opened", "acme/shop", 1), DateTimeOffset.Parse("2026-11-10T09:00:00Z"));
        await workspace.LogUsageAsync(Entry("b", "bug", "build_failed", "acme/api", 2), DateTimeOffset.Parse("2026-11-10T09:00:00Z"));
        await workspace.LogUsageAsync(Entry("c", "feature", "pr_opened", "acme/old", 3), DateTimeOffset.Parse("2026-10-01T09:00:00Z"));
        await workspace.LogUsageAsync(Entry("d", "revision", "revision_pushed", "acme/shop", 11), DateTimeOffset.Parse("2026-11-11T09:00:00Z"));
        await workspace.LogUsageAsync(Entry("e", "revision", "revision_pushed", "acme/shop", 11), DateTimeOffset.Parse("2026-11-20T09:00:00Z"));
        await workspace.LogUsageAsync(Entry("f", "revision", "revision_replied", "acme/shop", 11), DateTimeOffset.Parse("2026-11-12T09:00:00Z"));

        var scope = workspace.ReadPullScope(new ImprovementRequest("2026-W46", "2026-11-09", "2026-11-16", null));

        Assert.Equal(["acme/shop"], scope.Repositories);
        Assert.Equal([new LoggedPullKind("acme/shop", 1, "feature")], scope.Tickets);
        Assert.Equal([new LoggedRevisions("acme/shop", 11, 2)], scope.Revisions);
    }

    private sealed class Handler(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request)) });
        }
    }

    [Fact]
    public async Task Forgejo_is_asked_read_only_for_all_pull_requests_and_only_numbers_states_and_dates_are_kept()
    {
        var handler = new Handler(request => request.RequestUri!.Query.Contains("page=1")
            ? """[{"number":3,"state":"closed","merged":true,"title":"SECRET TITLE","body":"SECRET BODY","head":{"ref":"factory/issue-1"},"user":{"login":"factory-bot"},"created_at":"2026-11-10T10:00:00Z","merged_at":"2026-11-11T10:00:00Z"},{"state":"open"}]"""
            : "[]");
        var client = new ForgejoClient(new HttpClient(handler) { BaseAddress = new Uri("http://forgejo:3000/") }, "secret-token");

        var pulls = await client.ListAllPullRequestsAsync("acme", "shop", CancellationToken.None);

        var pull = Assert.Single(pulls); // the one without a number is skipped
        Assert.Equal((3, "closed", true, "factory/issue-1", "factory-bot"), (pull.Number, pull.State, pull.Merged, pull.HeadRef, pull.Author));
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T10:00:00Z"), pull.MergedAt);
        Assert.All(handler.Requests, request => Assert.StartsWith("GET /api/v1/repos/acme/shop/pulls?state=all", request));
        Assert.DoesNotContain("SECRET", pull.ToString());
    }
}

public sealed class PullReportTests
{
    private static readonly DateOnly From = new(2026, 11, 9);
    private static readonly DateOnly To = new(2026, 11, 16);

    private static ImprovementReport Report(IReadOnlyList<PullOutcome>? pulls) =>
        ImprovementReportBuilder.Build("2026-W46", From, To, [], [], [], false, pulls);

    private static PullOutcome Merged(int number, int revisions = 0, string kind = "feature") => new("acme/shop", number, kind, PullStates.Merged, revisions, 1.0);
    private static PullOutcome Closed(int number, string kind = "feature") => new("acme/shop", number, kind, PullStates.ClosedUnmerged, 0, null);
    private static PullOutcome Open(int number, string kind = "feature") => new("acme/shop", number, kind, PullStates.Open, 0, null);

    private static bool Flags(ImprovementReport report, string area) => report.Findings.Any(f => f.Area == area);

    [Fact]
    public void The_counts_are_shown_per_kind_and_in_all()
    {
        var report = Report([Merged(1, 1), Merged(2, 3), Closed(3), Open(4), Merged(5, 0, "bug")]);

        Assert.Contains("## What happened to the pull requests", report.Markdown);
        Assert.Contains("| bug | 1 | 1 | 0 | 0 | 0.0 | 1.0 |", report.Markdown);
        Assert.Contains("| feature | 4 | 2 | 1 | 1 | 2.0 | 1.0 |", report.Markdown);
        Assert.Contains("| **all** | 5 | 3 | 1 | 1 | 1.3 | 1.0 |", report.Markdown);
    }

    [Fact]
    public void With_fewer_than_twenty_pull_requests_it_says_so_instead_of_showing_percentages()
    {
        var few = Report([.. Enumerable.Range(1, 19).Select(i => Merged(i))]);
        var enough = Report([.. Enumerable.Range(1, 20).Select(i => Merged(i))]);

        Assert.Contains("too few to say what share is accepted", few.Markdown);
        Assert.DoesNotContain("100% merged", few.Markdown);
        Assert.Contains("100% merged, 0% closed without merging, 0% still open.", enough.Markdown);
        Assert.DoesNotContain("too few", enough.Markdown);
    }

    [Fact]
    public void A_week_with_no_pull_requests_and_one_that_could_not_be_read_say_so()
    {
        Assert.Contains("The factory opened no pull request this week.", Report([]).Markdown);
        Assert.Contains("The git host could not be asked", Report(null).Markdown);
        Assert.False(Report(null).HasFindings);
    }

    [Fact]
    public void Closed_without_merging_flags_at_thirty_percent_and_not_below_and_not_under_five_pull_requests()
    {
        // 3 of 10 = 30%: flags. 2 of 10 = 20%: does not. 2 of 4 = 50% but only 4 pull requests: too few.
        Assert.True(Flags(Report([Closed(1), Closed(2), Closed(3), .. Enumerable.Range(4, 7).Select(i => Merged(i))]), "Pull requests turned down"));
        Assert.False(Flags(Report([Closed(1), Closed(2), .. Enumerable.Range(3, 8).Select(i => Merged(i))]), "Pull requests turned down"));
        Assert.False(Flags(Report([Closed(1), Closed(2), Merged(3), Merged(4)]), "Pull requests turned down"));
        Assert.True(Flags(Report([Closed(1), Closed(2), Merged(3), Merged(4), Merged(5)]), "Pull requests turned down")); // 40% of 5: the smallest sample that counts
    }

    [Fact]
    public void Many_revisions_flag_at_an_average_of_two_and_not_below_and_not_under_five_merged()
    {
        Assert.True(Flags(Report([.. Enumerable.Range(1, 5).Select(i => Merged(i, 2))]), "Pull requests needing revisions"));
        Assert.False(Flags(Report([.. Enumerable.Range(1, 4).Select(i => Merged(i, 2)), Merged(5, 1)]), "Pull requests needing revisions"));  // 1.8
        Assert.False(Flags(Report([.. Enumerable.Range(1, 4).Select(i => Merged(i, 9))]), "Pull requests needing revisions"));                 // only 4 merged
        Assert.False(Flags(Report([.. Enumerable.Range(1, 5).Select(i => Open(i))]), "Pull requests needing revisions"));                     // nothing merged yet
    }

    [Fact]
    public void The_findings_are_counts_only_and_name_no_pull_request_text()
    {
        var report = Report([Closed(1), Closed(2), Closed(3), Merged(4), Merged(5)]);

        var finding = Assert.Single(report.Findings, f => f.Area == "Pull requests turned down");
        Assert.Contains("3 of 5 pull requests (60%)", finding.Evidence);
    }
}
