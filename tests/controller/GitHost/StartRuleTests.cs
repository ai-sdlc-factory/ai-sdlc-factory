using System.Text.Json;
using Temporalio.Testing;

namespace AiSdlcFactory.Controller.Tests;

/// <summary>The <c>start</c> setting: who may start a run by a label, read from <c>FACTORY_HOSTS</c>.</summary>
public sealed class StartRuleParseTests
{
    private static StartRule Rule(string json) => StartRule.Read(JsonDocument.Parse(json).RootElement, "start");

    private static string Error(string json) => Assert.Throws<InvalidOperationException>(() => Rule(json)).Message;

    [Theory]
    [InlineData("\"anyone\"", CommandLevel.Anyone)]
    [InlineData("\"write\"", CommandLevel.Write)]
    [InlineData("\"admin\"", CommandLevel.Admin)]
    [InlineData("\"none\"", CommandLevel.None)]
    public void A_level_as_text_is_the_whole_rule(string json, CommandLevel expected) => Assert.Equal(expected, Rule(json).Who);

    [Fact]
    public void A_list_of_people_or_teams_with_no_level_means_only_them_and_no_list_means_anyone_but_the_people_denied()
    {
        Assert.Equal(CommandLevel.None, Rule("""{"allow":["alice"]}""").Who);
        Assert.Equal(CommandLevel.None, Rule("""{"teams":["acme/maintainers"]}""").Who);
        Assert.Equal(CommandLevel.Anyone, Rule("""{"deny":["mallory"]}""").Who);
        Assert.Equal(CommandLevel.Anyone, Rule("{}").Who);
        Assert.Equal(CommandLevel.Write, Rule("""{"who":"write","teams":["acme/maintainers"]}""").Who);
    }

    [Fact]
    public void Every_setting_is_read()
    {
        var rule = Rule("""
            {"who":"write","allow":["Alice"],"deny":["mallory"],"teams":["acme/maintainers","acme/Maintainers","acme/triage"],"kinds":["feature","bug"],
             "refusal":"silent","message":"A maintainer has to label this first."}
            """);

        Assert.Equal(CommandLevel.Write, rule.Who);
        Assert.Contains("alice", rule.Allowed); // logins are the host's, case does not matter
        Assert.Contains("MALLORY", rule.Denied);
        Assert.Equal(["acme/maintainers", "acme/triage"], rule.TeamList); // the same team twice is one
        Assert.Equal(["feature", "bug"], rule.Kinds!.OrderBy(kind => kind == "feature" ? 0 : 1));
        Assert.True(rule.Silent);
        Assert.Equal("A maintainer has to label this first.", rule.Message);
    }

    [Fact]
    public void A_rule_is_open_only_when_nobody_is_held_back()
    {
        Assert.True(StartRule.Open.IsOpen);
        Assert.True(Rule("\"anyone\"").IsOpen);
        Assert.True(Rule("""{"allow":["alice"],"who":"anyone"}""").IsOpen); // a list only matters where the level lets in less
        Assert.False(Rule("\"write\"").IsOpen);
        Assert.False(Rule("""{"deny":["mallory"]}""").IsOpen);
        Assert.False(Rule("""{"teams":["acme/maintainers"]}""").IsOpen);
    }

    [Fact]
    public void A_rule_covers_every_kind_unless_it_names_some()
    {
        Assert.All(StartKinds.All, kind => Assert.True(Rule("\"write\"").Covers(kind)));
        var some = Rule("""{"who":"admin","kinds":["feature","plan"]}""");
        Assert.True(some.Covers("feature"));
        Assert.True(some.Covers("plan"));
        Assert.False(some.Covers("bug"));
        Assert.False(some.Covers("docs"));
        Assert.False(Rule("\"anyone\"").Covers("feature")); // an open rule judges nothing
    }

    [Theory]
    [InlineData("""{"who":"everyone"}""", "who must be one of anyone, write, admin, none")]
    [InlineData("""{"who":5}""", "who must be one of")]
    [InlineData("""{"allow":"alice"}""", "allow must be a list")]
    [InlineData("""{"teams":[]}""", "teams must be a list of 1 to 20 teams")]
    [InlineData("""{"teams":"acme/maintainers"}""", "teams must be a list")]
    [InlineData("""{"teams":["maintainers"]}""", "is not a team: write it organisation/team")]
    [InlineData("""{"teams":["acme/main tainers"]}""", "is not a team")]
    [InlineData("""{"teams":[5]}""", "is not a team")]
    [InlineData("""{"kinds":[]}""", "kinds must be a list")]
    [InlineData("""{"kinds":["feature","qa"]}""", "\"qa\" is not a kind of run (feature, bug, plan, incident, docs)")]
    [InlineData("""{"refusal":"loud"}""", "refusal must be \"comment\" or \"silent\"")]
    [InlineData("""{"message":""}""", "message must be one line of plain text of 1 to 500 characters")]
    [InlineData("""{"message":"two\nlines"}""", "message must be one line of plain text")]
    [InlineData("""{"message":5}""", "message must be one line")]
    [InlineData("""{"each":{}}""", "unknown setting \"each\" (who, allow, deny, teams, kinds, refusal, message)")]
    [InlineData("5", "must be a level (anyone, write, admin, none) or an object")]
    [InlineData("\"sometimes\"", "must be one of anyone, write, admin, none")]
    public void Anything_that_cannot_be_used_stops_the_start_with_a_message_that_says_what_is_wrong(string json, string expected) =>
        Assert.Contains(expected, Error(json));

    [Fact]
    public void Too_many_teams_and_a_message_that_is_too_long_are_refused()
    {
        var teams = string.Join(",", Enumerable.Range(1, StartRule.MaxTeams + 1).Select(i => $"\"acme/t{i}\""));

        Assert.Contains("1 to 20 teams", Error($$"""{"teams":[{{teams}}]}"""));
        Assert.Contains("1 to 500 characters", Error($$"""{"message":"{{new string('x', StartRule.MaxMessage + 1)}}"}"""));
        Assert.Equal(StartRule.MaxMessage, Rule($$"""{"message":"{{new string('x', StartRule.MaxMessage)}}"}""").Message!.Length);
    }

    [Fact]
    public void The_log_line_says_what_the_rule_asks_and_names_no_one()
    {
        Assert.Equal("anyone may start a run", StartRule.Open.Describe());
        Assert.Equal("a run starts only when set off by people with write access", Rule("\"write\"").Describe());
        Assert.Equal("a run for feature/bug starts only when set off by people with admin access, members of 2 teams, 1 named person, except 1 refused",
            Rule("""{"who":"admin","teams":["acme/a","acme/b"],"allow":["alice"],"deny":["mallory"],"kinds":["bug","feature"]}""").Describe());
        Assert.DoesNotContain("alice", Rule("""{"allow":["alice"]}""").Describe());
    }

    // ---- in FACTORY_HOSTS

    private static IReadOnlyList<HostDefinition> Hosts(string more) => HostsSetting.Parse(
        """[{"name":"github","type":"github","url":"https://github.com","token":"t","bot_login":"bot","webhook_secret":"0123456789abcdef0123456789abcdef",""" + more + "}]", needWebhookSecret: true);

    [Fact]
    public void A_host_without_the_setting_is_open()
    {
        var host = Hosts("\"commands\":\"write\"")[0];

        Assert.True(host.Start!.IsOpen);
        Assert.True(Hosts("\"name2\":1".Replace("\"name2\":1", "\"triggers\":{}"))[0].Start!.IsOpen);
    }

    [Fact]
    public void A_start_rule_for_the_host_and_one_of_its_own_for_a_repository_are_read_apart_from_the_commands()
    {
        var start = Hosts("""
            "commands": {"who": "write"},
            "start": {"teams": ["acme/maintainers"]},
            "repositories": {
              "acme/community": {"start": "anyone"},
              "acme/secret": {"commands": "admin", "start": {"who": "admin", "kinds": ["feature"]}},
              "acme/plain": {"commands": "admin"}
            }
            """)[0].Start!;

        Assert.False(start.IsOpen);
        Assert.Equal(CommandLevel.None, start.For("acme", "shop").Who); // the host's rule
        Assert.True(start.For("acme", "community").IsOpen); // its own rule replaces the host's
        Assert.Equal(CommandLevel.Admin, start.For("Acme", "SECRET").Who); // case does not matter
        Assert.Equal(CommandLevel.None, start.For("acme", "plain").Who); // an entry with only commands keeps the host's start rule
    }

    [Fact]
    public void A_repository_entry_with_only_a_start_rule_is_accepted_and_the_commands_of_the_host_are_unchanged()
    {
        var host = Hosts("""
            "commands": "write",
            "repositories": {"acme/shop": {"start": "write"}}
            """)[0];

        Assert.Equal(CommandLevel.Write, host.Commands!.For("acme", "shop").Who); // it falls back to the host's commands rule
        Assert.Equal(CommandLevel.Write, host.Start!.For("acme", "shop").Who);
    }

    [Fact]
    public void A_start_setting_that_cannot_be_used_names_the_host_and_the_repository()
    {
        Assert.Contains("host \"github\": start", Assert.Throws<InvalidOperationException>(() => Hosts("\"start\":{\"who\":\"all\"}")).Message);
        var repository = Assert.Throws<InvalidOperationException>(() => Hosts("\"repositories\":{\"acme/shop\":{\"start\":{\"kinds\":[\"qa\"]}}}")).Message;
        Assert.Contains("repositories.acme/shop.start", repository);
        Assert.Contains("only commands and start", Assert.Throws<InvalidOperationException>(() => Hosts("\"repositories\":{\"acme/shop\":{\"strat\":\"write\"}}")).Message);
    }
}

/// <summary>The decision about one person, from what the host said: pure, so every combination can be tried.</summary>
public sealed class StartPolicyTests
{
    private static StartRule Rule(string json) => StartRule.Read(JsonDocument.Parse(json).RootElement, "start");

    private static readonly IReadOnlyDictionary<string, bool> NoTeams = new Dictionary<string, bool>();

    private static IReadOnlyDictionary<string, bool> Teams(params (string Team, bool Member)[] answers) => answers.ToDictionary(pair => pair.Team, pair => pair.Member, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_kind_the_rule_does_not_cover_and_an_open_rule_let_everyone_start()
    {
        Assert.True(StartPolicy.Decide(Rule("""{"who":"admin","kinds":["feature"]}"""), "bug", "anyone", null, NoTeams).Allowed);
        Assert.True(StartPolicy.Decide(StartRule.Open, "feature", "anyone", null, NoTeams).Allowed);
        Assert.True(StartPolicy.Decide(Rule("""{"deny":["mallory"]}"""), "feature", "alice", null, NoTeams).Allowed);
    }

    [Fact]
    public void A_person_who_is_denied_is_refused_whatever_else_is_true()
    {
        var rule = Rule("""{"who":"write","allow":["mallory"],"deny":["mallory"],"teams":["acme/maintainers"]}""");

        var verdict = StartPolicy.Decide(rule, "feature", "Mallory", HostPermission.Admin, Teams(("acme/maintainers", true)));

        Assert.False(verdict.Allowed);
        Assert.False(verdict.CouldNotCheck); // a clear no, not a failure to find out
    }

    [Fact]
    public void A_person_who_is_named_is_let_in_whatever_their_access()
    {
        var rule = Rule("""{"who":"admin","allow":["alice"]}""");

        Assert.True(StartPolicy.Decide(rule, "feature", "ALICE", HostPermission.None, NoTeams).Allowed);
        Assert.True(StartPolicy.Decide(rule, "feature", "alice", null, NoTeams).Allowed);
    }

    [Fact]
    public void A_member_of_a_team_the_rule_names_is_let_in_and_a_member_of_another_is_not()
    {
        var rule = Rule("""{"teams":["acme/maintainers","acme/triage"]}""");

        Assert.True(StartPolicy.Decide(rule, "feature", "bob", null, Teams(("acme/maintainers", false), ("acme/triage", true))).Allowed);
        var refused = StartPolicy.Decide(rule, "feature", "bob", null, Teams(("acme/maintainers", false), ("acme/triage", false)));
        Assert.False(refused.Allowed);
        Assert.False(refused.CouldNotCheck);
    }

    [Theory]
    [InlineData(HostPermission.Write, true)]
    [InlineData(HostPermission.Admin, true)]
    [InlineData(HostPermission.Read, false)]
    [InlineData(HostPermission.None, false)]
    public void Write_access_is_what_the_level_write_asks_for(HostPermission permission, bool allowed) =>
        Assert.Equal(allowed, StartPolicy.Decide(Rule("\"write\""), "feature", "bob", permission, NoTeams).Allowed);

    [Theory]
    [InlineData(HostPermission.Admin, true)]
    [InlineData(HostPermission.Write, false)]
    public void Only_admin_access_is_what_the_level_admin_asks_for(HostPermission permission, bool allowed) =>
        Assert.Equal(allowed, StartPolicy.Decide(Rule("\"admin\""), "feature", "bob", permission, NoTeams).Allowed);

    [Fact]
    public void The_level_none_lets_in_only_the_people_and_teams_named()
    {
        var rule = Rule("""{"who":"none","allow":["alice"]}""");

        Assert.False(StartPolicy.Decide(rule, "feature", "bob", HostPermission.Admin, NoTeams).Allowed);
        Assert.True(StartPolicy.Decide(rule, "feature", "alice", null, NoTeams).Allowed);
    }

    [Fact]
    public void Access_and_a_team_each_let_a_person_in()
    {
        var rule = Rule("""{"who":"write","teams":["acme/maintainers"]}""");

        Assert.True(StartPolicy.Decide(rule, "feature", "writer", HostPermission.Write, Teams(("acme/maintainers", false))).Allowed);
        Assert.True(StartPolicy.Decide(rule, "feature", "member", HostPermission.Read, Teams(("acme/maintainers", true))).Allowed);
        Assert.False(StartPolicy.Decide(rule, "feature", "neither", HostPermission.Read, Teams(("acme/maintainers", false))).Allowed);
    }

    [Fact]
    public void A_person_the_host_could_not_say_anything_about_is_refused_and_the_verdict_says_it_could_not_check()
    {
        var level = Rule("\"write\"");
        var team = Rule("""{"teams":["acme/maintainers"]}""");

        Assert.True(StartPolicy.Decide(level, "feature", "bob", null, NoTeams).CouldNotCheck);
        Assert.True(StartPolicy.Decide(level, "feature", "bob", HostPermission.Unknown, NoTeams).CouldNotCheck);
        Assert.True(StartPolicy.Decide(team, "feature", "bob", null, NoTeams).CouldNotCheck); // the team answer is missing
        Assert.False(StartPolicy.Decide(level, "feature", "bob", null, NoTeams).Allowed);
        Assert.False(StartPolicy.Decide(Rule("""{"who":"write","teams":["acme/maintainers"]}"""), "feature", "bob", HostPermission.Read, NoTeams).Allowed); // access said no, the team is unknown
    }

    [Fact]
    public void A_start_with_nobody_named_as_the_person_is_refused()
    {
        var verdict = StartPolicy.Decide(Rule("\"write\""), "feature", "", HostPermission.Admin, NoTeams);

        Assert.False(verdict.Allowed);
        Assert.True(verdict.CouldNotCheck);
    }

    [Fact]
    public void The_verdict_carries_the_kind_the_silence_and_the_operators_words()
    {
        var verdict = StartPolicy.Decide(Rule("""{"who":"write","refusal":"silent","message":"Ask a maintainer."}"""), "bug", "bob", HostPermission.Read, NoTeams);

        Assert.Equal(("bug", true, "Ask a maintainer."), (verdict.Kind, verdict.Silent, verdict.Message));
    }

    [Fact]
    public void The_host_is_asked_about_access_only_for_a_level_that_depends_on_it_and_a_person_the_rule_does_not_settle()
    {
        Assert.True(StartPolicy.NeedsPermission(Rule("\"write\""), "bob"));
        Assert.True(StartPolicy.NeedsPermission(Rule("\"admin\""), "bob"));
        Assert.False(StartPolicy.NeedsPermission(Rule("\"none\""), "bob"));
        Assert.False(StartPolicy.NeedsPermission(Rule("""{"who":"write","allow":["bob"]}"""), "bob")); // named: no need to ask
        Assert.False(StartPolicy.NeedsPermission(Rule("""{"who":"write","deny":["bob"]}"""), "bob")); // denied: no need either
        Assert.False(StartPolicy.NeedsPermission(Rule("\"write\""), ""));
    }

    [Fact]
    public void The_teams_are_asked_about_only_while_they_could_change_the_answer()
    {
        var rule = Rule("""{"who":"write","teams":["acme/a","acme/b"],"allow":["named"],"deny":["denied"]}""");

        Assert.Equal(["acme/a", "acme/b"], StartPolicy.TeamsToAsk(rule, "bob", HostPermission.Read));
        Assert.Equal(["acme/a", "acme/b"], StartPolicy.TeamsToAsk(rule, "bob", null)); // access unknown: the teams may still let them in
        Assert.Empty(StartPolicy.TeamsToAsk(rule, "bob", HostPermission.Write)); // access already lets them in
        Assert.Empty(StartPolicy.TeamsToAsk(rule, "named", null));
        Assert.Empty(StartPolicy.TeamsToAsk(rule, "denied", null));
        Assert.Empty(StartPolicy.TeamsToAsk(rule, "", null));
    }

    [Fact]
    public void What_the_rule_asks_is_said_in_words_that_name_no_person()
    {
        Assert.Equal("write access to the repository", StartPolicy.Needs(Rule("\"write\"")));
        Assert.Equal("admin access to the repository", StartPolicy.Needs(Rule("\"admin\"")));
        Assert.Equal("membership of acme/maintainers or acme/triage", StartPolicy.Needs(Rule("""{"teams":["acme/maintainers","acme/triage"]}""")));
        Assert.Equal("write access to the repository, or membership of acme/maintainers, or being on the list of people the operator named",
            StartPolicy.Needs(Rule("""{"who":"write","teams":["acme/maintainers"],"allow":["alice"]}""")));
        Assert.DoesNotContain("alice", StartPolicy.Needs(Rule("""{"allow":["alice"]}""")));
        Assert.Equal("someone the operator allows", StartPolicy.Needs(Rule("""{"who":"none"}""")));
    }

    [Fact]
    public void The_message_a_ticket_is_told_names_the_requirement_and_not_the_person_and_the_operators_words_replace_it()
    {
        var plain = StartPolicy.Decide(Rule("""{"who":"write","teams":["acme/maintainers"]}"""), "feature", "bob", HostPermission.Read, Teams(("acme/maintainers", false)));
        var own = StartPolicy.Decide(Rule("""{"who":"write","message":"Ask a maintainer."}"""), "feature", "bob", HostPermission.Read, NoTeams);
        var unknown = StartPolicy.Decide(Rule("""{"who":"write","message":"Ask a maintainer."}"""), "feature", "bob", null, NoTeams);

        Assert.Contains("I did not start a feature run", TicketComments.StartRefused(plain));
        Assert.Contains("write access to the repository, or membership of acme/maintainers", TicketComments.StartRefused(plain));
        Assert.DoesNotContain("bob", TicketComments.StartRefused(plain));
        Assert.Equal("Ask a maintainer.", TicketComments.StartRefused(own));
        // When the host could not say, the operator's words would not be true: the person is told that instead.
        Assert.Contains("could not find out", TicketComments.StartRefused(unknown));
        Assert.DoesNotContain("Ask a maintainer.", TicketComments.StartRefused(unknown));
    }
}

/// <summary>The activity that decides: what it asks the host, how often, and what happens when the host cannot say.</summary>
public sealed class CheckStartActivityTests
{
    private static WorkerSettings Settings(string start) => new()
    {
        Hosts = [new HostSettings("forgejo", "forgejo", "http://forgejo:3000", "t", "bot", null, null, HostStartRules.Read(JsonDocument.Parse($"{{{start}}}").RootElement, "test"))],
    };

    private static Task<StartVerdict> Check(GitHostActivities activities, string login, string kind = "feature", string repo = "shop") =>
        new ActivityEnvironment().RunAsync(() => activities.CheckStartAsync(new StartCheck("forgejo", "acme", repo, login, kind)));

    [Fact]
    public async Task With_no_rule_or_a_kind_it_does_not_cover_the_host_is_never_asked()
    {
        var host = new CheckCommandActivityTests.CountingHost(HostPermission.None);

        Assert.True((await Check(new GitHostActivities(host, Settings("")), "bob")).Allowed);
        Assert.True((await Check(new GitHostActivities(host, Settings("\"start\":{\"who\":\"admin\",\"kinds\":[\"feature\"]}")), "bob", kind: "bug")).Allowed);
        Assert.Equal((0, 0), (host.Lookups, host.TeamLookups));
    }

    [Fact]
    public async Task A_level_asks_for_the_persons_access_once_and_keeps_the_answer()
    {
        var host = new CheckCommandActivityTests.CountingHost(HostPermission.Write);
        var activities = new GitHostActivities(host, Settings("\"start\":\"write\""));

        Assert.True((await Check(activities, "bob")).Allowed);
        Assert.True((await Check(activities, "bob", kind: "bug")).Allowed);

        Assert.Equal(1, host.Lookups);
    }

    [Fact]
    public async Task Too_little_access_is_a_clear_no_that_says_what_is_needed()
    {
        var verdict = await Check(new GitHostActivities(new CheckCommandActivityTests.CountingHost(HostPermission.Read), Settings("\"start\":\"write\"")), "bob");

        Assert.False(verdict.Allowed);
        Assert.False(verdict.CouldNotCheck);
        Assert.Equal(("feature", "write access to the repository"), (verdict.Kind, verdict.Needs));
    }

    [Fact]
    public async Task A_host_that_fails_makes_the_answer_no_and_it_is_not_kept()
    {
        var host = new CheckCommandActivityTests.CountingHost(HostPermission.Write, fail: true);
        var activities = new GitHostActivities(host, Settings("\"start\":\"write\""));

        var verdict = await Check(activities, "bob");
        await Check(activities, "bob");

        Assert.False(verdict.Allowed);
        Assert.True(verdict.CouldNotCheck);
        Assert.Equal(2, host.Lookups); // asked again the next time
    }

    [Fact]
    public async Task A_person_who_is_named_or_denied_is_decided_without_asking_the_host()
    {
        var host = new CheckCommandActivityTests.CountingHost(HostPermission.None);
        var activities = new GitHostActivities(host, Settings("\"start\":{\"who\":\"write\",\"allow\":[\"alice\"],\"deny\":[\"mallory\"],\"teams\":[\"acme/maintainers\"]}"));

        Assert.True((await Check(activities, "Alice")).Allowed);
        Assert.False((await Check(activities, "mallory")).Allowed);

        Assert.Equal((0, 0), (host.Lookups, host.TeamLookups));
    }

    [Fact]
    public async Task A_member_of_a_named_team_is_let_in_and_the_answer_is_kept()
    {
        var host = new CheckCommandActivityTests.CountingHost(HostPermission.None, teamMember: true);
        var activities = new GitHostActivities(host, Settings("\"start\":{\"teams\":[\"acme/maintainers\"]}"));

        Assert.True((await Check(activities, "bob")).Allowed);
        Assert.True((await Check(activities, "bob", kind: "docs")).Allowed);

        Assert.Equal((0, 1), (host.Lookups, host.TeamLookups)); // only teams here, so access is not asked about
    }

    [Fact]
    public async Task A_person_who_is_not_in_the_team_is_a_clear_no_and_a_team_the_host_cannot_say_is_not_taken_for_one()
    {
        var no = await Check(new GitHostActivities(new CheckCommandActivityTests.CountingHost(HostPermission.None, teamMember: false), Settings("\"start\":{\"teams\":[\"acme/maintainers\"]}")), "bob");
        var unknown = await Check(new GitHostActivities(new CheckCommandActivityTests.CountingHost(HostPermission.None, teamMember: null), Settings("\"start\":{\"teams\":[\"acme/maintainers\"]}")), "bob");
        var host = new CheckCommandActivityTests.CountingHost(HostPermission.None, failTeam: true);
        var failing = new GitHostActivities(host, Settings("\"start\":{\"teams\":[\"acme/maintainers\"]}"));

        Assert.False(no.Allowed);
        Assert.False(no.CouldNotCheck);
        Assert.False(unknown.Allowed);
        Assert.True(unknown.CouldNotCheck);
        Assert.True((await Check(failing, "bob")).CouldNotCheck);
        await Check(failing, "bob");
        Assert.Equal(2, host.TeamLookups); // a failure is not kept
    }

    [Fact]
    public async Task The_teams_are_not_asked_about_when_the_persons_access_already_lets_them_in_and_asking_stops_at_the_first_yes()
    {
        var access = new CheckCommandActivityTests.CountingHost(HostPermission.Write, teamMember: false);
        Assert.True((await Check(new GitHostActivities(access, Settings("\"start\":{\"who\":\"write\",\"teams\":[\"acme/a\"]}")), "bob")).Allowed);
        Assert.Equal(0, access.TeamLookups);

        var yes = new CheckCommandActivityTests.CountingHost(HostPermission.Read, teamMember: true);
        Assert.True((await Check(new GitHostActivities(yes, Settings("\"start\":{\"who\":\"write\",\"teams\":[\"acme/a\",\"acme/b\",\"acme/c\"]}")), "bob")).Allowed);
        Assert.Equal(1, yes.TeamLookups);

        var all = new CheckCommandActivityTests.CountingHost(HostPermission.Read, teamMember: false);
        Assert.False((await Check(new GitHostActivities(all, Settings("\"start\":{\"who\":\"write\",\"teams\":[\"acme/a\",\"acme/b\"]}")), "bob")).Allowed);
        Assert.Equal(2, all.TeamLookups);
    }

    [Fact]
    public async Task A_rule_for_one_repository_does_not_reach_the_others()
    {
        var host = new CheckCommandActivityTests.CountingHost(HostPermission.Read);
        var activities = new GitHostActivities(host, Settings("\"repositories\":{\"acme/secret\":{\"start\":\"admin\"}}"));

        Assert.False((await Check(activities, "bob", repo: "secret")).Allowed);
        Assert.True((await Check(activities, "bob", repo: "shop")).Allowed);
    }
}
