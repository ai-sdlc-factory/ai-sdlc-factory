using System.Net;
using System.Text;
using System.Text.Json;
using Temporalio.Testing;

namespace AiSdlcFactory.Controller.Tests;

public sealed class FinerCommandRuleParsingTests
{
    private static CommandRule Rule(string json) => CommandRule.Read(JsonDocument.Parse(json).RootElement, "test");

    [Fact]
    public void A_command_can_have_a_rule_of_its_own_as_a_level_or_an_object()
    {
        var rule = Rule("""{"who":"write","each":{"approve":"admin","hotfix":{"who":"none","allow":["alice"]}}}""");

        Assert.Equal(CommandLevel.Admin, rule.EachCommand["approve"].Who);
        Assert.Equal(CommandLevel.None, rule.EachCommand["hotfix"].Who);
        Assert.Contains("ALICE", rule.EachCommand["hotfix"].Allowed);
        Assert.False(rule.IsOpen);
    }

    [Fact]
    public void A_rule_with_a_rule_for_one_command_is_not_open_even_when_the_whole_is()
    {
        Assert.False(Rule("""{"each":{"approve":"admin"}}""").IsOpen);
        Assert.False(Rule("""{"project":true}""").IsOpen);
        Assert.True(Rule("""{"each":{}}""").IsOpen);
    }

    [Fact]
    public void A_quiet_rule_keeps_its_commands_quiet_too_unless_they_say_otherwise()
    {
        var rule = Rule("""{"who":"write","refusal":"silent","each":{"approve":"admin","qa":{"who":"admin","refusal":"comment"}}}""");

        Assert.True(rule.EachCommand["approve"].Silent);
        Assert.True(rule.EachCommand["qa"].Silent); // "comment" is the default, so it cannot be told from not saying: the quiet whole wins
    }

    [Theory]
    [InlineData("""{"each":{"aprove":"admin"}}""", "not a command name")]
    [InlineData("""{"each":[]}""", "object")]
    [InlineData("""{"each":{"approve":{"each":{"qa":"admin"}}}}""", "unknown setting")]
    [InlineData("""{"each":{"approve":{"project":true}}}""", "unknown setting")]
    [InlineData("""{"each":{"approve":"wite"}}""", "one of")]
    [InlineData("""{"project":"yes"}""", "true or false")]
    public void A_command_rule_that_cannot_be_used_is_refused_and_says_where(string json, string message)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Rule(json));

        Assert.Contains("test", error.Message);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Every_name_a_rule_can_use_is_one_the_reader_can_produce()
    {
        var produced = new[] { "ask", "setup", "retry", "fix", "update", "qa", "review", "factory", "approve", "option", "hotfix", "resolved", "reply", "changes" };

        Assert.Equal(produced.Order(), CommandNames.All.Order());
    }

    private static ProjectCommandRule Project(string json) => ProjectCommandRule.Read(JsonDocument.Parse(json).RootElement, "commands");

    [Fact]
    public void A_project_can_ask_for_a_level_and_name_people_to_refuse()
    {
        Assert.Equal(CommandLevel.Admin, Project("\"admin\"").Who);
        var rule = Project("""{"who":"write","deny":["Mallory"]}""");
        Assert.Equal(CommandLevel.Write, rule.Who);
        Assert.Contains("mallory", rule.Deny);
        Assert.True(rule.Tightens);
        Assert.False(ProjectCommandRule.None.Tightens);
    }

    [Theory]
    [InlineData("\"anyone\"")]
    [InlineData("\"none\"")]
    [InlineData("""{"allow":["alice"]}""")]
    [InlineData("""{"who":"write","each":{}}""")]
    [InlineData("""{"refusal":"silent"}""")]
    [InlineData("5")]
    public void A_project_cannot_let_anyone_in_or_close_the_repository_to_everyone(string json) =>
        Assert.Throws<InvalidOperationException>(() => Project(json));

    [Fact]
    public void The_describing_line_mentions_what_else_is_in_force_and_names_no_one()
    {
        var line = Rule("""{"who":"write","each":{"approve":"admin"},"project":true,"allow":["alice"]}""").Describe();

        Assert.Equal("commands need write access, and 1 named; 1 commands have a rule of their own; the repository may tighten it", line);
    }
}

public sealed class FinerCommandPolicyTests
{
    private static CommandRule Rule(string json) => CommandRule.Read(JsonDocument.Parse(json).RootElement, "test");

    [Fact]
    public void A_command_needs_the_whole_rule_and_then_its_own()
    {
        var rule = Rule("""{"who":"write","each":{"approve":"admin"}}""");

        Assert.True(CommandPolicy.Decide(rule, "alice", HostPermission.Write, ["qa"]).Allowed);               // the whole rule is enough for another command
        Assert.False(CommandPolicy.Decide(rule, "alice", HostPermission.Write, ["approve"]).Allowed);          // /approve needs admin on top
        Assert.True(CommandPolicy.Decide(rule, "alice", HostPermission.Admin, ["approve"]).Allowed);
        Assert.False(CommandPolicy.Decide(rule, "alice", HostPermission.Read, ["approve"]).Allowed);           // and a person who fails the whole rule fails everything
    }

    [Fact]
    public void A_comment_with_several_commands_has_to_pass_the_rule_of_each()
    {
        var rule = Rule("""{"each":{"hotfix":"admin"}}""");

        var verdict = CommandPolicy.Decide(rule, "alice", HostPermission.Write, ["option", "hotfix"]);

        Assert.Equal((false, CommandRefusal.TooLittleAccess, "admin"), (verdict.Allowed, verdict.Refusal, verdict.Needs));
    }

    [Fact]
    public void A_command_can_be_closed_to_all_but_named_people_while_the_rest_stays_open()
    {
        var rule = Rule("""{"each":{"hotfix":{"who":"none","allow":["alice"]}}}""");

        Assert.True(CommandPolicy.Decide(rule, "bob", null, ["qa"]).Allowed);
        Assert.False(CommandPolicy.Decide(rule, "bob", null, ["hotfix"]).Allowed);
        Assert.True(CommandPolicy.Decide(rule, "alice", null, ["hotfix"]).Allowed);
        Assert.False(CommandPolicy.NeedsPermission(rule, "bob", ["hotfix"]));
    }

    [Fact]
    public void Being_named_by_the_whole_rule_does_not_get_past_a_commands_own_stricter_rule()
    {
        var rule = Rule("""{"who":"none","allow":["alice"],"each":{"approve":"admin"}}""");

        Assert.True(CommandPolicy.Decide(rule, "alice", HostPermission.Read, ["qa"]).Allowed);
        Assert.False(CommandPolicy.Decide(rule, "alice", HostPermission.Read, ["approve"]).Allowed);
        Assert.True(CommandPolicy.Decide(rule, "alice", HostPermission.Admin, ["approve"]).Allowed);
    }

    [Fact]
    public void A_commands_refusal_is_quiet_when_its_rule_is_quiet()
    {
        var rule = Rule("""{"refusal":"silent","each":{"approve":"admin"}}""");

        var verdict = CommandPolicy.Decide(rule, "alice", HostPermission.Write, ["approve"]);

        Assert.False(verdict.Allowed);
        Assert.True(verdict.Silent);
    }

    [Fact]
    public void A_commands_rule_asks_the_host_only_for_the_commands_that_have_one()
    {
        var rule = Rule("""{"each":{"approve":"admin"}}""");

        Assert.True(CommandPolicy.NeedsPermission(rule, "alice", ["approve"]));
        Assert.False(CommandPolicy.NeedsPermission(rule, "alice", ["qa"]));
        Assert.False(CommandPolicy.NeedsPermission(rule, "alice", null));
    }

    // ---- what the repository may tighten

    private static ProjectCommandRule Project(string json) => ProjectCommandRule.Read(JsonDocument.Parse(json).RootElement, "commands");

    [Fact]
    public void A_project_can_tighten_the_level_and_refuse_people_but_only_where_the_operator_allowed_it()
    {
        var rule = Rule("""{"project":true}""");

        Assert.True(CommandPolicy.NeedsPermission(rule, "alice", [], Project("\"admin\"")));
        Assert.False(CommandPolicy.Decide(rule, "alice", HostPermission.Write, [], Project("\"admin\"")).Allowed);
        Assert.True(CommandPolicy.Decide(rule, "alice", HostPermission.Admin, [], Project("\"admin\"")).Allowed);
        Assert.Equal(CommandRefusal.Denied, CommandPolicy.Decide(rule, "Mallory", HostPermission.Admin, [], Project("""{"deny":["mallory"]}""")).Refusal);
        Assert.False(CommandPolicy.NeedsPermission(rule, "mallory", [], Project("""{"deny":["mallory"]}""")));
    }

    [Fact]
    public void A_project_that_says_nothing_changes_nothing()
    {
        var rule = Rule("""{"project":true}""");

        Assert.True(CommandPolicy.Decide(rule, "alice", null, [], ProjectCommandRule.None).Allowed);
        Assert.False(CommandPolicy.NeedsPermission(rule, "alice", [], ProjectCommandRule.None));
        Assert.True(CommandPolicy.Decide(rule, "alice", null, [], null).Allowed);
    }

    [Fact]
    public void A_person_the_operator_named_is_not_touched_by_the_project()
    {
        var rule = Rule("""{"who":"none","allow":["alice"],"project":true}""");

        Assert.True(CommandPolicy.Decide(rule, "alice", HostPermission.Read, [], Project("\"admin\"")).Allowed);
        Assert.True(CommandPolicy.Decide(rule, "alice", null, [], Project("""{"deny":["alice"]}""")).Allowed);
        Assert.False(CommandPolicy.NeedsPermission(rule, "alice", [], Project("\"admin\"")));
    }

    [Fact]
    public void The_project_is_on_top_of_the_operators_rule_never_instead_of_it()
    {
        var rule = Rule("""{"who":"admin","project":true}""");

        Assert.False(CommandPolicy.Decide(rule, "alice", HostPermission.Write, [], Project("\"write\"")).Allowed); // the project asks for less than the operator: the operator's rule holds
        Assert.True(CommandPolicy.Decide(rule, "alice", HostPermission.Admin, [], Project("\"write\"")).Allowed);
    }

    [Fact]
    public void A_project_that_asks_for_access_the_host_cannot_say_refuses()
    {
        var rule = Rule("""{"project":true}""");

        var verdict = CommandPolicy.Decide(rule, "alice", HostPermission.Unknown, [], Project("\"write\""));

        Assert.Equal(CommandRefusal.CouldNotCheck, verdict.Refusal);
    }
}

public sealed class CommandNameReaderTests
{
    private static RepoEvent Comment(string body, bool pull = false) =>
        new("forgejo", "d", pull ? "issue_comment" : "issue_comment", RepoEventKind.Commented, Owner: "acme", Repo: "shop", Number: 1, IsPullRequest: pull, CommentBody: body, CommentAuthor: "bob", Labels: ["feature"]);

    private static RoutingDecision Decision(RoutingAction action) => new(action, "test");

    [Theory]
    [InlineData("/ask where is it?", "ask")]
    [InlineData("/ASK", "ask")]
    [InlineData("/setup", "setup")]
    [InlineData("/retry restart", "retry")]
    [InlineData("/fix make it faster", "fix")]
    [InlineData("/revise it", "fix")]
    [InlineData("/update", "update")]
    [InlineData("/qa full", "qa")]
    [InlineData("/review", "review")]
    [InlineData("/factory feature", "factory")]
    [InlineData("  /approve 2 3", "approve")]
    [InlineData("/choose 2", "option")]
    [InlineData("/hotfix", "hotfix")]
    [InlineData("/resolved", "resolved")]
    public void A_command_is_named_as_a_rule_names_it(string body, string expected) =>
        Assert.Equal([expected], CommandNameReader.Of(Comment(body), Decision(RoutingAction.ReplyToFeature)));

    [Fact]
    public void A_comment_with_several_commands_names_each_once_in_order()
    {
        var names = CommandNameReader.Of(Comment("Looks good.\n/option 2\n/hotfix\n/option 3\n/resolved"), Decision(RoutingAction.ReplyToFeature));

        Assert.Equal(["option", "hotfix", "resolved"], names);
    }

    [Fact]
    public void A_plain_answer_to_an_agent_is_a_reply_and_a_word_that_is_not_a_command_is_not_one()
    {
        Assert.Equal(["reply"], CommandNameReader.Of(Comment("Yes, use the second one."), Decision(RoutingAction.ReplyToFeature)));
        Assert.Equal(["reply"], CommandNameReader.Of(Comment("/unknownthing please"), Decision(RoutingAction.ReplyToFeature)));
        Assert.Equal(["reply"], CommandNameReader.Of(Comment("/reply"), Decision(RoutingAction.ReplyToFeature)));  // "reply" and "changes" are names, not slash commands
    }

    [Fact]
    public void Without_a_slash_word_the_decision_names_the_command()
    {
        Assert.Equal(["ask"], CommandNameReader.Of(Comment("anything"), Decision(RoutingAction.AskQuestion)));
        Assert.Equal(["qa"], CommandNameReader.Of(Comment("go"), Decision(RoutingAction.ConfirmQa)));
        Assert.Equal(["fix"], CommandNameReader.Of(Comment("do it"), Decision(RoutingAction.RevisePullRequest)));
    }

    [Fact]
    public void A_review_that_asks_for_changes_is_changes()
    {
        var review = new RepoEvent("forgejo", "d", "pull_request_rejected", RepoEventKind.ChangesRequested, Owner: "acme", Repo: "shop", Number: 5, IsPullRequest: true, Actor: "bob");

        Assert.Equal(["changes"], CommandNameReader.Of(review, Decision(RoutingAction.RevisePullRequest)));
    }
}

public sealed class ReadFileTests
{
    private sealed class Answering(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string File(string text, int? size = null, string type = "file", bool newlines = false)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        if (newlines)
        {
            encoded = string.Join("\n", Enumerable.Range(0, (encoded.Length + 59) / 60).Select(i => encoded.Substring(i * 60, Math.Min(60, encoded.Length - i * 60))));
        }
        return JsonSerializer.Serialize(new { type, encoding = "base64", content = encoded, size = size ?? text.Length });
    }

    private static IEnumerable<(string Name, Func<HttpMessageHandler, IGitHost> Make, string Prefix)> Hosts() =>
    [
        ("forgejo", handler => new ForgejoClient(new HttpClient(handler) { BaseAddress = new Uri("http://forgejo:3000/") }, "t"), "/api/v1/repos/acme/shop/contents/.factory/project.json"),
        ("github", handler => new GitHubClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, "t"), "/repos/acme/shop/contents/.factory/project.json"),
    ];

    [Fact]
    public async Task A_file_is_read_from_the_contents_answer_on_both_hosts_even_with_line_breaks_in_it()
    {
        foreach (var (name, make, path) in Hosts())
        {
            var handler = new Answering(HttpStatusCode.OK, File("""{"commands":"write","note":"ünïcode"}""" + new string(' ', 120), newlines: true));

            var text = await make(handler).ReadFileAsync("acme", "shop", ".factory/project.json", CancellationToken.None);

            Assert.True(text!.StartsWith("""{"commands":"write","note":"ünïcode"}"""), name);
            Assert.Equal([path], handler.Paths);
        }
    }

    [Fact]
    public async Task A_missing_file_is_null_and_a_folder_or_a_too_large_file_is_not_read()
    {
        foreach (var (name, make, _) in Hosts())
        {
            Assert.Null(await make(new Answering(HttpStatusCode.NotFound, "{}")).ReadFileAsync("acme", "shop", "x", CancellationToken.None));
            Assert.Null(await make(new Answering(HttpStatusCode.OK, File("[]", type: "dir"))).ReadFileAsync("acme", "shop", "x", CancellationToken.None));
            Assert.Null(await make(new Answering(HttpStatusCode.OK, File("{}", size: GitHostLimits.MaxFileBytes + 1))).ReadFileAsync("acme", "shop", "x", CancellationToken.None));
            Assert.Null(await make(new Answering(HttpStatusCode.OK, """{"type":"file","encoding":"base64","content":"%%%not base64%%%","size":3}""")).ReadFileAsync("acme", "shop", "x", CancellationToken.None));
            Assert.NotNull(name);
        }
    }

    [Fact]
    public async Task A_failing_host_is_an_error_and_a_path_cannot_climb()
    {
        foreach (var (_, make, _) in Hosts())
        {
            await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
                () => make(new Answering(HttpStatusCode.InternalServerError, "{}")).ReadFileAsync("acme", "shop", "x", CancellationToken.None));

            var handler = new Answering(HttpStatusCode.NotFound, "{}");
            await make(handler).ReadFileAsync("acme", "shop", "a b/c#d.json", CancellationToken.None);
            Assert.EndsWith("/contents/a%20b/c%23d.json", Assert.Single(handler.Paths));
        }
    }
}

public sealed class ProjectCommandsKeyTests
{
    private static IReadOnlyList<string> Warnings(string json) => ProjectKeys.Warnings(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void The_commands_key_is_known_and_a_usable_one_has_no_warning()
    {
        Assert.Empty(Warnings("""{"image":"python","commands":"admin"}"""));
        Assert.Empty(Warnings("""{"image":"python","commands":{"who":"write","deny":["mallory"]}}"""));
    }

    [Fact]
    public void An_unusable_one_is_a_warning_that_says_why_and_the_run_goes_on()
    {
        var warning = Assert.Single(Warnings("""{"image":"python","commands":{"allow":["alice"]}}"""));

        Assert.Contains("`commands`", warning);
        Assert.Contains("only tighten", warning);
    }
}
