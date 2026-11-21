using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Temporalio.Testing;

namespace DevFoundry.Controller.Tests;

public sealed class CommandRuleParsingTests
{
    private static CommandRule Rule(string json) => CommandRule.Read(JsonDocument.Parse(json).RootElement, "test");

    [Fact]
    public void A_level_as_text_is_the_whole_rule()
    {
        Assert.Equal(CommandLevel.Write, Rule("\"write\"").Who);
        Assert.Equal(CommandLevel.None, Rule("\"none\"").Who);
        Assert.True(Rule("\"anyone\"").IsOpen);
    }

    [Fact]
    public void An_object_has_a_level_two_lists_of_logins_and_a_way_to_refuse()
    {
        var rule = Rule("""{"who":"write","allow":["Alice","bob"],"deny":["mallory"],"refusal":"silent"}""");

        Assert.Equal(CommandLevel.Write, rule.Who);
        Assert.True(rule.Allowed.Contains("alice") && rule.Allowed.Contains("BOB")); // case does not matter
        Assert.Contains("MALLORY", rule.Denied);
        Assert.True(rule.Silent);
        Assert.False(rule.IsOpen is true && rule.Who != CommandLevel.Anyone);
    }

    [Fact]
    public void Nothing_set_is_open_and_a_deny_list_alone_is_not()
    {
        Assert.True(Rule("{}").IsOpen);
        Assert.False(Rule("""{"deny":["mallory"]}""").IsOpen);
        Assert.True(Rule("""{"allow":["alice"]}""").IsOpen); // the allow list only matters where the level holds people back
    }

    [Theory]
    [InlineData("\"wite\"", "one of anyone, write, admin, none")]
    [InlineData("5", "level")]
    [InlineData("""{"who":"nobody"}""", "who")]
    [InlineData("""{"allow":"alice"}""", "allow")]
    [InlineData("""{"allow":["a b"]}""", "login that cannot be used")]
    [InlineData("""{"deny":[7]}""", "login that cannot be used")]
    [InlineData("""{"refusal":"loud"}""", "silent")]
    [InlineData("""{"oops":1}""", "unknown setting")]
    public void A_setting_that_cannot_be_used_is_refused_and_says_where(string json, string message)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Rule(json));

        Assert.Contains("test", error.Message);
        Assert.Contains(message, error.Message);
    }

    private static HostCommandRules Host(string json) => HostCommandRules.Read(JsonDocument.Parse(json).RootElement, "host \"h\"");

    [Fact]
    public void A_host_without_a_setting_is_open_everywhere()
    {
        var rules = Host("""{"name":"h"}""");

        Assert.True(rules.IsOpen);
        Assert.True(rules.For("acme", "shop").IsOpen);
    }

    [Fact]
    public void A_repository_has_its_own_rule_and_the_rest_follow_the_hosts_and_case_does_not_matter()
    {
        var rules = Host("""{"commands":"write","repositories":{"Acme/Public-Site":{"commands":{"who":"none","allow":["alice"]}}}}""");

        Assert.Equal(CommandLevel.None, rules.For("acme", "public-site").Who);
        Assert.Equal(CommandLevel.Write, rules.For("acme", "other").Who);
        Assert.False(rules.IsOpen);
    }

    [Theory]
    [InlineData("""{"repositories":[]}""", "owner/name")]
    [InlineData("""{"repositories":{"nonsense":{}}}""", "owner/name")]
    [InlineData("""{"repositories":{"a/b":"write"}}""", "object")]
    [InlineData("""{"repositories":{"a/b":{"limits":1}}}""", "unknown setting")]
    [InlineData("""{"repositories":{"a/b":{"commands":"wite"}}}""", "one of")]
    public void A_repository_setting_that_cannot_be_used_stops_the_start_and_says_where(string json, string message)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Host(json));

        Assert.Contains("host \"h\"", error.Message);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void The_settings_are_read_with_the_hosts_and_reach_the_controllers_host_settings()
    {
        var settings = WorkerSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FACTORY_HOSTS"] = """[{"name":"github","type":"github","url":"https://github.com","commands":{"who":"admin","deny":["mallory"]}},{"name":"forgejo","url":"http://forgejo:3000"}]""",
        }).Build());

        Assert.Equal(CommandLevel.Admin, settings.HostNamed("github").Commands!.Default.Who);
        Assert.True(settings.HostNamed("forgejo").Commands!.IsOpen);
    }

    [Fact]
    public void A_rule_describes_itself_without_naming_anyone()
    {
        Assert.Equal("anyone who can comment may use commands", CommandRule.Open.Describe());
        Assert.Equal("commands need write access, 1 named are let in and 2 refused", Rule("""{"who":"write","allow":["alice"],"deny":["a","b"]}""").Describe());
        Assert.Equal("commands only for the 2 named", Rule("""{"who":"none","allow":["alice","bob"]}""").Describe());
        Assert.DoesNotContain("alice", Rule("""{"who":"none","allow":["alice"]}""").Describe());
    }
}

public sealed class CommandPolicyTests
{
    private static CommandRule Rule(CommandLevel who, string[]? allow = null, string[]? deny = null, bool silent = false) =>
        new(who, allow is null ? null : new HashSet<string>(allow, StringComparer.OrdinalIgnoreCase), deny is null ? null : new HashSet<string>(deny, StringComparer.OrdinalIgnoreCase), silent);

    [Fact]
    public void With_no_rule_everyone_may_and_the_host_is_not_asked()
    {
        Assert.True(CommandPolicy.Decide(CommandRule.Open, "anyone", null).Allowed);
        Assert.False(CommandPolicy.NeedsPermission(CommandRule.Open, "anyone"));
        Assert.True(CommandPolicy.Decide(CommandRule.Open, "", null).Allowed);
    }

    [Theory]
    [InlineData(CommandLevel.Write, HostPermission.Admin, true)]
    [InlineData(CommandLevel.Write, HostPermission.Write, true)]
    [InlineData(CommandLevel.Write, HostPermission.Read, false)]
    [InlineData(CommandLevel.Write, HostPermission.None, false)]
    [InlineData(CommandLevel.Admin, HostPermission.Admin, true)]
    [InlineData(CommandLevel.Admin, HostPermission.Write, false)]
    public void A_level_asks_for_that_much_access_or_more(CommandLevel who, HostPermission has, bool allowed) =>
        Assert.Equal(allowed, CommandPolicy.Decide(Rule(who), "alice", has).Allowed);

    [Fact]
    public void When_the_host_cannot_say_the_answer_is_no_and_says_so()
    {
        foreach (HostPermission? unknown in new HostPermission?[] { null, HostPermission.Unknown })
        {
            var verdict = CommandPolicy.Decide(Rule(CommandLevel.Write), "alice", unknown);

            Assert.False(verdict.Allowed);
            Assert.Equal(CommandRefusal.CouldNotCheck, verdict.Refusal);
        }
    }

    [Fact]
    public void A_refused_login_is_refused_even_with_admin_access_and_even_when_listed_as_allowed()
    {
        var rule = Rule(CommandLevel.Write, allow: ["mallory"], deny: ["Mallory"]);

        var verdict = CommandPolicy.Decide(rule, "mallory", HostPermission.Admin);

        Assert.Equal((false, CommandRefusal.Denied), (verdict.Allowed, verdict.Refusal));
        Assert.False(CommandPolicy.NeedsPermission(rule, "mallory")); // decided without asking the host
    }

    [Fact]
    public void A_named_person_passes_without_the_access_and_without_a_lookup()
    {
        var rule = Rule(CommandLevel.Admin, allow: ["Alice"]);

        Assert.True(CommandPolicy.Decide(rule, "alice", HostPermission.None).Allowed);
        Assert.False(CommandPolicy.NeedsPermission(rule, "alice"));
        Assert.True(CommandPolicy.NeedsPermission(rule, "bob"));
    }

    [Fact]
    public void The_level_none_lets_in_only_the_named()
    {
        var rule = Rule(CommandLevel.None, allow: ["alice"]);

        Assert.True(CommandPolicy.Decide(rule, "alice", null).Allowed);
        var other = CommandPolicy.Decide(rule, "admin-on-the-host", HostPermission.Admin);
        Assert.Equal((false, CommandRefusal.NotListed), (other.Allowed, other.Refusal));
        Assert.False(CommandPolicy.NeedsPermission(rule, "bob"));
    }

    [Fact]
    public void A_deny_list_alone_keeps_out_only_those_named()
    {
        var rule = Rule(CommandLevel.Anyone, deny: ["mallory"]);

        Assert.True(CommandPolicy.Decide(rule, "alice", null).Allowed);
        Assert.False(CommandPolicy.Decide(rule, "mallory", null).Allowed);
    }

    [Fact]
    public void An_unknown_person_is_refused_by_any_rule_that_holds_people_back()
    {
        Assert.False(CommandPolicy.Decide(Rule(CommandLevel.Write), "", HostPermission.Admin).Allowed);
        Assert.False(CommandPolicy.Decide(Rule(CommandLevel.Anyone, deny: ["x"]), "", null).Allowed);
    }

    [Fact]
    public void A_refusal_carries_what_the_rule_needed_and_whether_to_stay_silent()
    {
        var verdict = CommandPolicy.Decide(Rule(CommandLevel.Admin, silent: true), "alice", HostPermission.Write);

        Assert.Equal(("admin", true, CommandRefusal.TooLittleAccess), (verdict.Needs, verdict.Silent, verdict.Refusal));
        Assert.Contains("needs admin access", TicketComments.CommandRefused(verdict));
    }

    [Fact]
    public void Every_refusal_has_a_text_that_names_no_one()
    {
        foreach (var why in new[] { CommandRefusal.Denied, CommandRefusal.NotListed, CommandRefusal.TooLittleAccess, CommandRefusal.CouldNotCheck })
        {
            var text = TicketComments.CommandRefused(new CommandVerdict(false, why, "write", false));

            Assert.StartsWith("I did not run this command", text);
            Assert.DoesNotContain("alice", text);
        }
    }
}

public sealed class PermissionCacheTests
{
    private sealed class Clock { public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-21T12:00:00Z"); }

    [Fact]
    public void An_answer_is_kept_for_the_lifetime_and_then_asked_again_and_case_does_not_matter()
    {
        var clock = new Clock();
        var cache = new PermissionCache(TimeSpan.FromMinutes(5), () => clock.Now);

        cache.Keep("Acme", "Shop", "Alice", HostPermission.Write);
        clock.Now += TimeSpan.FromMinutes(4);
        Assert.True(cache.TryGet("acme", "shop", "alice", out var kept));
        Assert.Equal(HostPermission.Write, kept);
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.False(cache.TryGet("acme", "shop", "alice", out _));
    }

    [Fact]
    public void An_unknown_answer_is_never_kept()
    {
        var cache = new PermissionCache(TimeSpan.FromMinutes(5));

        cache.Keep("a", "b", "c", HostPermission.Unknown);

        Assert.False(cache.TryGet("a", "b", "c", out _));
    }

    [Fact]
    public void A_lifetime_of_zero_keeps_nothing()
    {
        var cache = new PermissionCache(TimeSpan.Zero);

        cache.Keep("a", "b", "c", HostPermission.Admin);

        Assert.False(cache.TryGet("a", "b", "c", out _));
    }

    [Fact]
    public void Another_person_or_repository_is_another_answer()
    {
        var cache = new PermissionCache(TimeSpan.FromMinutes(5));
        cache.Keep("a", "b", "alice", HostPermission.Admin);

        Assert.False(cache.TryGet("a", "b", "bob", out _));
        Assert.False(cache.TryGet("a", "other", "alice", out _));
    }

    [Fact]
    public void The_lifetime_is_a_setting_with_a_range()
    {
        WorkerSettings Load(string? value) => WorkerSettings.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(TestHosts.Setting).AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { ["FACTORY_PERMISSION_CACHE_SECONDS"] = value }).Build());

        Assert.Equal(300, Load(null).PermissionCacheSeconds);
        Assert.Equal(0, Load("0").PermissionCacheSeconds);
        Assert.Equal(3600, Load("3600").PermissionCacheSeconds);
        Assert.Contains("FACTORY_PERMISSION_CACHE_SECONDS", Assert.Throws<InvalidOperationException>(() => Load("7200")).Message);
        Assert.Throws<InvalidOperationException>(() => Load("soon"));
    }
}

public sealed class CheckCommandActivityTests
{
    private sealed class CountingHost(HostPermission answer, bool fail = false, string? projectFile = null, bool failFile = false) : IGitHost
    {
        public int Lookups { get; private set; }
        public int FileReads { get; private set; }

        public Task<string?> ReadFileAsync(string owner, string repo, string path, CancellationToken ct)
        {
            FileReads++;
            return failFile ? throw new HttpRequestException("host down") : Task.FromResult(projectFile);
        }

        public Task<HostPermission> GetPermissionAsync(string owner, string repo, string login, CancellationToken ct)
        {
            Lookups++;
            return fail ? throw new HttpRequestException("host down") : Task.FromResult(answer);
        }

        // Nothing else is used by the check.
        public Task PostIssueCommentAsync(string owner, string repo, int issueNumber, string body, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> RecentCommentBodiesAsync(string owner, string repo, int issueNumber, int count, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> PostNoticeOnceAsync(string owner, string repo, int issueNumber, string body, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> PostNoticeUniqueAsync(string owner, string repo, int issueNumber, string body, CancellationToken ct) => throw new NotSupportedException();
        public Task<IssueInfo> CreateIssueAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labelNames, CancellationToken ct) => throw new NotSupportedException();
        public Task<PullRequestDetails> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<PullChanges> GetPullChangesAsync(string owner, string repo, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReviewDetails>> GetUnaddressedReviewsAsync(string owner, string repo, int number, string botLogin, DateTimeOffset? since, CancellationToken ct) => throw new NotSupportedException();
        public Task<RevisionHistory> GetRevisionHistoryAsync(string owner, string repo, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RequestReviewAsync(string owner, string repo, int number, IReadOnlyList<string> reviewers, CancellationToken ct) => throw new NotSupportedException();
        public Task<int?> FindFactoryPullRequestAsync(string owner, string repo, int ticket, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PullSummary>> ListAllPullRequestsAsync(string owner, string repo, CancellationToken ct) => throw new NotSupportedException();
        public Task<PullRequestInfo> CreatePullRequestAsync(string owner, string repo, string head, string baseBranch, string title, string body, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<(string Owner, string Repo)>> ListWritableRepositoriesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string owner, string repo, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IssueIsClosedAsync(string owner, string repo, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> OpenPullRequestBranchesAsync(string owner, string repo, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteBranchAsync(string owner, string repo, string branch, CancellationToken ct) => throw new NotSupportedException();
    }

    private static WorkerSettings Settings(string commands) => new()
    {
        Hosts = [new HostSettings("forgejo", "forgejo", "http://forgejo:3000", "t", "bot", null, HostCommandRules.Read(JsonDocument.Parse($"{{{commands}}}").RootElement, "test"))],
    };

    private static Task<CommandVerdict> Check(GitHostActivities activities, string login, string owner = "acme") =>
        new ActivityEnvironment().RunAsync(() => activities.CheckCommandAsync(new CommandCheck("forgejo", owner, "shop", login)));

    [Fact]
    public async Task With_no_rule_the_host_is_never_asked()
    {
        var host = new CountingHost(HostPermission.None);

        var verdict = await Check(new GitHostActivities(host, Settings("")), "stranger");

        Assert.True(verdict.Allowed);
        Assert.Equal(0, host.Lookups);
    }

    [Fact]
    public async Task A_level_asks_the_host_once_and_keeps_the_answer_for_the_next_command()
    {
        var host = new CountingHost(HostPermission.Write);
        var activities = new GitHostActivities(host, Settings("\"commands\":\"write\""));

        var first = await Check(activities, "alice");
        var second = await Check(activities, "Alice");

        Assert.True(first.Allowed && second.Allowed);
        Assert.Equal(1, host.Lookups);
    }

    [Fact]
    public async Task A_lifetime_of_zero_asks_every_time()
    {
        var host = new CountingHost(HostPermission.Write);
        var activities = new GitHostActivities(host, Settings("\"commands\":\"write\""), new PermissionCache(TimeSpan.Zero));

        await Check(activities, "alice");
        await Check(activities, "alice");

        Assert.Equal(2, host.Lookups);
    }

    [Fact]
    public async Task Named_people_are_decided_without_the_host_and_the_repositorys_own_rule_wins()
    {
        var host = new CountingHost(HostPermission.Admin);
        var activities = new GitHostActivities(host, Settings("""
            "commands":"anyone","repositories":{"acme/shop":{"commands":{"who":"admin","allow":["alice"],"deny":["mallory"]}}}
            """));

        Assert.True((await Check(activities, "alice")).Allowed);
        Assert.False((await Check(activities, "mallory")).Allowed);
        Assert.Equal(0, host.Lookups);
        Assert.True((await Check(activities, "bob")).Allowed); // admin on the host
        Assert.Equal(1, host.Lookups);
        Assert.True((await Check(activities, "carol", owner: "other")).Allowed); // another repository of the open host
        Assert.Equal(1, host.Lookups);
    }

    [Fact]
    public async Task A_host_that_fails_makes_the_answer_no_and_is_asked_again_next_time()
    {
        var host = new CountingHost(HostPermission.Write, fail: true);
        var activities = new GitHostActivities(host, Settings("\"commands\":\"write\""));

        var first = await Check(activities, "alice");
        var second = await Check(activities, "alice");

        Assert.Equal((false, CommandRefusal.CouldNotCheck), (first.Allowed, first.Refusal));
        Assert.False(second.Allowed);
        Assert.Equal(2, host.Lookups);
    }

    [Fact]
    public async Task A_host_that_cannot_say_is_refused_and_not_remembered()
    {
        var host = new CountingHost(HostPermission.Unknown);
        var activities = new GitHostActivities(host, Settings("\"commands\":\"write\""));

        var verdict = await Check(activities, "alice");
        await Check(activities, "alice");

        Assert.Equal(CommandRefusal.CouldNotCheck, verdict.Refusal);
        Assert.Equal(2, host.Lookups);
    }

    private static Task<CommandVerdict> CheckCommand(GitHostActivities activities, string login, params string[] commands) =>
        new ActivityEnvironment().RunAsync(() => activities.CheckCommandAsync(new CommandCheck("forgejo", "acme", "shop", login, commands)));

    [Fact]
    public async Task A_rule_for_one_command_asks_the_host_only_when_that_command_is_used()
    {
        var host = new CountingHost(HostPermission.Write);
        var activities = new GitHostActivities(host, Settings("\"commands\":{\"each\":{\"approve\":\"admin\"}}"));

        Assert.True((await CheckCommand(activities, "alice", "qa")).Allowed);
        Assert.Equal(0, host.Lookups);
        Assert.False((await CheckCommand(activities, "alice", "approve")).Allowed);
        Assert.Equal(1, host.Lookups);
    }

    [Fact]
    public async Task The_project_file_is_not_read_unless_the_operator_allowed_it()
    {
        var host = new CountingHost(HostPermission.Write, projectFile: """{"commands":"admin"}""");
        var activities = new GitHostActivities(host, Settings("\"commands\":{\"who\":\"write\"}"));

        Assert.True((await CheckCommand(activities, "alice", "qa")).Allowed);

        Assert.Equal(0, host.FileReads);
    }

    [Fact]
    public async Task Where_allowed_the_project_tightens_and_the_file_is_read_once_for_a_burst()
    {
        var host = new CountingHost(HostPermission.Write, projectFile: """{"commands":"admin"}""");
        var activities = new GitHostActivities(host, Settings("\"commands\":{\"project\":true}"));

        var first = await CheckCommand(activities, "alice", "qa");
        var second = await CheckCommand(activities, "alice", "fix");

        Assert.Equal((false, CommandRefusal.TooLittleAccess), (first.Allowed, first.Refusal));
        Assert.False(second.Allowed);
        Assert.Equal(1, host.FileReads);
        Assert.Equal(1, host.Lookups);
    }

    [Fact]
    public async Task A_project_without_the_key_or_without_a_file_or_with_an_unusable_key_changes_nothing()
    {
        foreach (var file in new string?[] { """{"image":"python"}""", null, """{"commands":{"allow":["alice"]}}""", "not json at all" })
        {
            var host = new CountingHost(HostPermission.Read, projectFile: file);
            var activities = new GitHostActivities(host, Settings("\"commands\":{\"project\":true}"));

            Assert.True((await CheckCommand(activities, "alice", "qa")).Allowed, file);
            Assert.Equal(0, host.Lookups);
        }
    }

    [Fact]
    public async Task A_project_file_that_cannot_be_read_refuses_for_now_and_is_read_again_next_time()
    {
        var host = new CountingHost(HostPermission.Write, failFile: true);
        var activities = new GitHostActivities(host, Settings("\"commands\":{\"project\":true}"));

        var first = await CheckCommand(activities, "alice", "qa");
        await CheckCommand(activities, "alice", "qa");

        Assert.Equal((false, CommandRefusal.CouldNotCheck), (first.Allowed, first.Refusal));
        Assert.Equal(2, host.FileReads);
    }

    [Fact]
    public async Task A_person_the_operator_named_passes_the_project_without_asking_the_host()
    {
        var host = new CountingHost(HostPermission.None, projectFile: """{"commands":{"who":"admin","deny":["alice"]}}""");
        var activities = new GitHostActivities(host, Settings("\"commands\":{\"allow\":[\"alice\"],\"project\":true}"));

        Assert.True((await CheckCommand(activities, "alice", "qa")).Allowed);
        Assert.Equal(0, host.Lookups);
    }
}
