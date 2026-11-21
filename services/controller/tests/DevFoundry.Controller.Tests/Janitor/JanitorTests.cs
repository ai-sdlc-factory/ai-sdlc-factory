using Microsoft.Extensions.Configuration;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevFoundry.Controller.Tests;

public sealed class JanitorRulesTests
{
    [Theory]
    [InlineData("feature-acme-shop-1-abc", true)]
    [InlineData("bug-acme-shop-2-abc", true)]
    [InlineData("incident-acme-shop-3-abc", true)]
    [InlineData("revision-acme-shop-4-abc", true)]
    [InlineData("qa-acme-shop-4-abc", true)]
    [InlineData("my-own-folder", false)]
    [InlineData("backup", false)]
    [InlineData("features", false)]
    public void Only_folders_named_after_a_run_are_the_factorys_own(string name, bool ours) =>
        Assert.Equal(ours, JanitorRules.IsOurWorkspace(name));

    [Theory]
    [InlineData(3, false, 2, true)]
    [InlineData(2, false, 2, true)]
    [InlineData(1, false, 2, false)]   // too recent
    [InlineData(30, true, 2, false)]   // its run is still running
    public void A_checkout_goes_only_when_its_run_is_gone_and_it_is_old_enough(int daysUntouched, bool running, int days, bool stale) =>
        Assert.Equal(stale, JanitorRules.CheckoutIsStale(TimeSpan.FromDays(daysUntouched), running, days));

    [Theory]
    [InlineData("factory/issue-8-wip", 8)]
    [InlineData("factory/issue-8-wip-2", 8)]
    [InlineData("factory/issue-12-wip-13", 12)]
    [InlineData("factory/issue-8", null)]          // a pull request branch is never a work-in-progress branch
    [InlineData("factory/issue-8-2", null)]
    [InlineData("main", null)]
    [InlineData("feature/issue-8-wip", null)]
    [InlineData("factory/issue-8-wip/extra", null)]
    [InlineData("xfactory/issue-8-wip", null)]
    public void Only_the_factorys_work_in_progress_branches_are_recognised(string branch, int? expected) =>
        Assert.Equal(expected, JanitorRules.WipIssueNumber(branch));

    [Theory]
    [InlineData(29, true, false, false, WipAction.Keep)]     // too young
    [InlineData(40, true, true, true, WipAction.Keep)]       // an open pull request uses it
    [InlineData(40, true, false, false, WipAction.Report)]   // deletion not allowed
    [InlineData(40, false, false, true, WipAction.Report)]   // ticket still open
    [InlineData(40, true, false, true, WipAction.Delete)]
    public void A_branch_is_deleted_only_when_all_the_cautious_conditions_hold(int age, bool closed, bool openPull, bool allowed, WipAction expected) =>
        Assert.Equal(expected, JanitorRules.DecideWip(TimeSpan.FromDays(age), closed, openPull, 30, allowed));
}

public sealed class JanitorRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class Store(params WorkspaceEntry[] entries) : IWorkspaceStore
    {
        public List<string> Deleted { get; } = [];
        public string? FailOn { get; set; }
        public IReadOnlyList<WorkspaceEntry> List() => entries;
        public Task DeleteAsync(string name)
        {
            if (name == FailOn) { throw new IOException("busy"); }
            Deleted.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class Running(params string[] ids) : IRunningWorkflows
    {
        public bool Fails { get; set; }
        public Task<IReadOnlySet<string>> RunningIdsAsync(CancellationToken ct) =>
            Fails ? throw new InvalidOperationException("temporal is down") : Task.FromResult<IReadOnlySet<string>>(ids.ToHashSet());
    }

    private sealed class FakeHost : IJanitorGitHost
    {
        public List<(string, string)> Repos { get; } = [("acme", "shop")];
        public Dictionary<string, List<BranchInfo>> Branches { get; } = new() { ["acme/shop"] = [] };
        public HashSet<int> ClosedIssues { get; } = [];
        public HashSet<string> OpenPullBranches { get; } = [];
        public List<string> Deleted { get; } = [];
        public bool FailListing { get; set; }
        public Task<IReadOnlyList<(string Owner, string Repo)>> ListWritableRepositoriesAsync(CancellationToken ct) =>
            FailListing ? throw new InvalidOperationException("the host is down") : Task.FromResult<IReadOnlyList<(string, string)>>(Repos);
        public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string owner, string repo, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BranchInfo>>(Branches.TryGetValue($"{owner}/{repo}", out var list) ? list : throw new InvalidOperationException("no such repo"));
        public Task<bool> IssueIsClosedAsync(string owner, string repo, int number, CancellationToken ct) => Task.FromResult(ClosedIssues.Contains(number));
        public Task<IReadOnlySet<string>> OpenPullRequestBranchesAsync(string owner, string repo, CancellationToken ct) => Task.FromResult<IReadOnlySet<string>>(OpenPullBranches);
        public Task DeleteBranchAsync(string owner, string repo, string branch, CancellationToken ct) { Deleted.Add($"{owner}/{repo}:{branch}"); return Task.CompletedTask; }
    }

    private static JanitorRunner Make(Store store, Running running, FakeHost host, bool deleteWip = false, string token = "t") =>
        new(store, running, host, new WorkerSettings { Hosts = TestHosts.One(token), JanitorCheckoutDays = 2, JanitorWipDays = 30, JanitorDeleteWip = deleteWip },
            NullLogger<JanitorRunner>.Instance, () => Now);

    private static WorkspaceEntry Entry(string name, int daysAgo) => new(name, Now.AddDays(-daysAgo));

    [Fact]
    public async Task Old_checkouts_of_finished_runs_are_removed_but_running_young_and_foreign_ones_are_not()
    {
        var store = new Store(Entry("feature-acme-shop-1-a", 5), Entry("feature-acme-shop-2-a", 5), Entry("bug-acme-shop-3-a", 1), Entry("somebody-elses-folder", 90));
        var runner = Make(store, new Running("feature-acme-shop-2-a"), new FakeHost());

        var report = await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["feature-acme-shop-1-a"], report.RemovedCheckouts);
        Assert.Equal(["feature-acme-shop-1-a"], store.Deleted);
    }

    [Fact]
    public async Task When_the_running_workflows_cannot_be_listed_nothing_is_removed()
    {
        var store = new Store(Entry("feature-acme-shop-1-a", 50));

        var report = await Make(store, new Running { Fails = true }, new FakeHost()).RunOnceAsync(CancellationToken.None);

        Assert.Empty(store.Deleted);
        Assert.Contains("no checkout was removed", Assert.Single(report.Problems));
    }

    [Fact]
    public async Task A_checkout_that_cannot_be_removed_is_reported_and_the_others_still_go()
    {
        var store = new Store(Entry("feature-acme-shop-1-a", 5), Entry("feature-acme-shop-2-a", 5)) { FailOn = "feature-acme-shop-1-a" };

        var report = await Make(store, new Running(), new FakeHost()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(["feature-acme-shop-2-a"], store.Deleted);
        Assert.Contains("feature-acme-shop-1-a", Assert.Single(report.Problems));
    }

    [Fact]
    public async Task Old_wip_branches_are_only_listed_by_default_even_when_the_ticket_is_closed()
    {
        var host = new FakeHost();
        host.Branches["acme/shop"].AddRange([new("factory/issue-4-wip", Now.AddDays(-40)), new("factory/issue-5-wip", Now.AddDays(-3)), new("main", Now.AddDays(-400))]);
        host.ClosedIssues.Add(4);

        var report = await Make(new Store(), new Running(), host).RunOnceAsync(CancellationToken.None);

        Assert.Equal(["acme/shop: factory/issue-4-wip"], report.OldBranches);
        Assert.Empty(report.DeletedBranches);
        Assert.Empty(host.Deleted);
    }

    [Fact]
    public async Task With_deletion_allowed_only_old_branches_of_closed_tickets_without_an_open_pull_request_are_deleted()
    {
        var host = new FakeHost();
        host.Branches["acme/shop"].AddRange(
        [
            new("factory/issue-4-wip", Now.AddDays(-40)),    // closed, old: deleted
            new("factory/issue-6-wip", Now.AddDays(-40)),    // ticket still open: kept (listed)
            new("factory/issue-7-wip", Now.AddDays(-40)),    // open pull request: kept
            new("factory/issue-8-wip", Now.AddDays(-5)),     // young: kept
            new("factory/issue-4", Now.AddDays(-400)),       // a pull request branch: never touched
        ]);
        host.ClosedIssues.UnionWith([4, 7, 8]);
        host.OpenPullBranches.Add("factory/issue-7-wip");

        var report = await Make(new Store(), new Running(), host, deleteWip: true).RunOnceAsync(CancellationToken.None);

        Assert.Equal(["acme/shop:factory/issue-4-wip"], host.Deleted);
        Assert.Equal(["acme/shop: factory/issue-4-wip"], report.DeletedBranches);
        Assert.Equal(["acme/shop: factory/issue-4-wip", "acme/shop: factory/issue-6-wip"], report.OldBranches);
    }

    [Fact]
    public async Task A_problem_with_one_repository_does_not_stop_the_others()
    {
        var host = new FakeHost();
        host.Repos.Insert(0, ("acme", "broken"));
        host.Branches["acme/shop"].Add(new("factory/issue-4-wip", Now.AddDays(-40)));

        var report = await Make(new Store(), new Running(), host).RunOnceAsync(CancellationToken.None);

        Assert.Contains("acme/broken", Assert.Single(report.Problems));
        Assert.Equal(["acme/shop: factory/issue-4-wip"], report.OldBranches);
    }

    [Fact]
    public async Task Without_a_host_token_branches_are_not_looked_at_but_checkouts_still_are()
    {
        var host = new FakeHost { FailListing = true };
        var store = new Store(Entry("feature-acme-shop-1-a", 5));

        var report = await Make(store, new Running(), host, token: "").RunOnceAsync(CancellationToken.None);

        Assert.Equal(["feature-acme-shop-1-a"], store.Deleted);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public async Task A_host_outage_is_a_problem_in_the_report_not_a_crash()
    {
        var report = await Make(new Store(), new Running(), new FakeHost { FailListing = true }).RunOnceAsync(CancellationToken.None);

        Assert.Contains("could not list the repositories", Assert.Single(report.Problems));
    }
}

public sealed class WorkspaceListingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "jl-" + Guid.NewGuid().ToString("N"));

    public WorkspaceListingTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private GitWorkspace Workspace() => new(new WorkerSettings { Hosts = TestHosts.One(), WorkspaceRoot = root });

    private void Touch(string path, int daysAgo)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText($"{path}/f.txt", "x");
        File.SetLastWriteTimeUtc($"{path}/f.txt", DateTime.UtcNow.AddDays(-daysAgo));
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysAgo));
    }

    [Fact]
    public void Folders_of_a_run_are_found_in_all_three_places_and_the_newest_touch_counts()
    {
        Touch($"{root}/feature-acme-shop-1-a", 10);
        Touch($"{root}/.base/feature-acme-shop-1-a", 3);
        Touch($"{root}/.records/bug-acme-shop-2-a", 8);
        Touch($"{root}/.base", 0); // the container folder itself is not a run
        Directory.CreateDirectory($"{root}/.hidden-thing");

        var entries = Workspace().List();

        Assert.Equal(["bug-acme-shop-2-a", "feature-acme-shop-1-a"], entries.Select(e => e.Name));
        var feature = entries.Single(e => e.Name == "feature-acme-shop-1-a");
        Assert.InRange((DateTimeOffset.UtcNow - feature.LastTouched).TotalDays, 2.9, 3.2);
    }

    [Fact]
    public async Task Deleting_a_run_removes_its_checkout_clone_and_record_folder_and_nothing_else()
    {
        Touch($"{root}/feature-acme-shop-1-a", 10);
        Touch($"{root}/.base/feature-acme-shop-1-a", 10);
        Touch($"{root}/.records/feature-acme-shop-1-a", 10);
        Touch($"{root}/feature-acme-shop-2-a", 10);
        Touch($"{root}/my-own-folder", 10);

        await Workspace().DeleteAsync("feature-acme-shop-1-a");

        Assert.False(Directory.Exists($"{root}/feature-acme-shop-1-a"));
        Assert.False(Directory.Exists($"{root}/.base/feature-acme-shop-1-a"));
        Assert.False(Directory.Exists($"{root}/.records/feature-acme-shop-1-a"));
        Assert.True(Directory.Exists($"{root}/feature-acme-shop-2-a"));
        Assert.True(Directory.Exists($"{root}/my-own-folder"));
    }

    [Fact]
    public void A_symbolic_link_in_the_root_is_not_listed_so_it_can_never_be_followed()
    {
        Touch($"{root}/elsewhere", 10);
        Directory.CreateSymbolicLink($"{root}/feature-acme-shop-9-a", $"{root}/elsewhere");

        Assert.DoesNotContain(Workspace().List(), e => e.Name == "feature-acme-shop-9-a");
    }
}

public sealed class JanitorForgejoClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static ForgejoClient Client(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://forgejo:3000/") }, "secret-token");

    [Fact]
    public async Task Only_repositories_the_bot_can_push_to_are_listed()
    {
        var handler = new Handler(request => request.RequestUri!.Query.Contains("page=1")
            ? (HttpStatusCode.OK, """{"data":[{"full_name":"acme/shop","permissions":{"push":true}},{"full_name":"other/readonly","permissions":{"push":false}},{"full_name":"x/nopermissions"}]}""")
            : (HttpStatusCode.OK, """{"data":[]}"""));

        var repos = await Client(handler).ListWritableRepositoriesAsync(CancellationToken.None);

        Assert.Equal([("acme", "shop")], repos);
    }

    [Fact]
    public async Task Branches_are_listed_with_the_time_of_their_last_commit()
    {
        var handler = new Handler(request => request.RequestUri!.Query.Contains("page=1")
            ? (HttpStatusCode.OK, """[{"name":"main","commit":{"timestamp":"2026-10-01T10:00:00Z"}},{"name":"factory/issue-4-wip","commit":{"timestamp":"2026-09-01T00:00:00+03:00"}},{"name":"broken"}]""")
            : (HttpStatusCode.OK, "[]"));

        var branches = await Client(handler).ListBranchesAsync("acme", "shop", CancellationToken.None);

        Assert.Equal(["main", "factory/issue-4-wip"], branches.Select(b => b.Name));
        Assert.Equal(DateTimeOffset.Parse("2026-08-31T21:00:00Z"), branches[1].LastCommit);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"state":"closed"}""", true)]
    [InlineData(HttpStatusCode.OK, """{"state":"open"}""", false)]
    [InlineData(HttpStatusCode.NotFound, "{}", false)]
    public async Task A_ticket_counts_as_closed_only_when_forgejo_says_so(HttpStatusCode status, string body, bool closed) =>
        Assert.Equal(closed, await Client(new Handler(_ => (status, body))).IssueIsClosedAsync("acme", "shop", 4, CancellationToken.None));

    [Fact]
    public async Task The_heads_of_open_pull_requests_are_collected()
    {
        var handler = new Handler(request => request.RequestUri!.Query.Contains("page=1")
            ? (HttpStatusCode.OK, """[{"head":{"ref":"factory/issue-7"}},{"head":{"ref":"factory/issue-9-wip"}}]""")
            : (HttpStatusCode.OK, "[]"));

        var heads = await Client(handler).OpenPullRequestBranchesAsync("acme", "shop", CancellationToken.None);

        Assert.Equal(["factory/issue-7", "factory/issue-9-wip"], heads.Order());
    }

    [Fact]
    public async Task A_branch_with_a_slash_is_deleted_by_its_encoded_name_and_a_missing_one_is_fine()
    {
        var handler = new Handler(_ => (HttpStatusCode.NotFound, "{}"));

        await Client(handler).DeleteBranchAsync("acme", "shop", "factory/issue-4-wip", CancellationToken.None);

        Assert.Equal("DELETE /api/v1/repos/acme/shop/branches/factory%2Fissue-4-wip", Assert.Single(handler.Requests));
    }
}

public sealed class JanitorSettingsTests
{
    private static WorkerSettings Settings(params (string Key, string Value)[] values) =>
        WorkerSettings.FromConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(TestHosts.Setting).AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

    [Fact]
    public void The_janitor_is_on_and_cautious_by_default()
    {
        var settings = Settings();

        Assert.True(settings.JanitorEnabled);
        Assert.Equal((24, 2, 30), (settings.JanitorIntervalHours, settings.JanitorCheckoutDays, settings.JanitorWipDays));
        Assert.False(settings.JanitorDeleteWip);
    }

    [Fact]
    public void Every_janitor_setting_can_be_changed()
    {
        var settings = Settings(("FACTORY_JANITOR", "false"), ("FACTORY_JANITOR_INTERVAL_HOURS", "6"), ("FACTORY_JANITOR_CHECKOUT_DAYS", "7"),
            ("FACTORY_JANITOR_WIP_DAYS", "90"), ("FACTORY_JANITOR_DELETE_WIP", "true"));

        Assert.False(settings.JanitorEnabled);
        Assert.Equal((6, 7, 90), (settings.JanitorIntervalHours, settings.JanitorCheckoutDays, settings.JanitorWipDays));
        Assert.True(settings.JanitorDeleteWip);
    }

    [Theory]
    [InlineData("FACTORY_JANITOR_INTERVAL_HOURS", "0")]
    [InlineData("FACTORY_JANITOR_INTERVAL_HOURS", "169")]
    [InlineData("FACTORY_JANITOR_CHECKOUT_DAYS", "0")]
    [InlineData("FACTORY_JANITOR_WIP_DAYS", "366")]
    [InlineData("FACTORY_JANITOR_WIP_DAYS", "soon")]
    [InlineData("FACTORY_JANITOR", "maybe")]
    public void A_setting_out_of_range_stops_the_start_with_its_name(string key, string value) =>
        Assert.Contains(key, Assert.Throws<InvalidOperationException>(() => Settings((key, value))).Message);
}
