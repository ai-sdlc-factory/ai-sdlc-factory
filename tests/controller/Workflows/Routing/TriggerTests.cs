using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AiSdlcFactory.Controller.Tests;

public sealed class TriggerRulesTests
{
    private static RepoEvent Ticket(string[] labels, string? itemType = null) =>
        new("forgejo", "d", "issues", RepoEventKind.TicketOpened, Owner: "acme", Repo: "shop", Number: 7, Labels: labels, ItemType: itemType);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Map(params (string Kind, string[] Names)[] items) =>
        items.ToDictionary(item => item.Kind, item => (IReadOnlyList<string>)item.Names);

    [Theory]
    [InlineData("feature", "feature")]
    [InlineData("Bug", "bug")]
    [InlineData("incident", "incident")]
    [InlineData("plan", "plan")]
    [InlineData("docs", "docs")]
    [InlineData("question", null)]
    public void Out_of_the_box_the_known_labels_pick_the_workflow(string label, string? kind) =>
        Assert.Equal(kind, TriggerRules.Default.KindOf(Ticket([label])));

    [Fact]
    public void Several_kinds_on_one_ticket_resolve_the_way_they_always_did()
    {
        Assert.Equal("incident", TriggerRules.Default.KindOf(Ticket(["feature", "incident"])));
        Assert.Equal("plan", TriggerRules.Default.KindOf(Ticket(["feature", "plan"])));
        Assert.Equal("feature", TriggerRules.Default.KindOf(Ticket(["bug", "feature"])));
        Assert.Equal("bug", TriggerRules.Default.KindOf(Ticket(["docs", "bug"])));
    }

    [Fact]
    public void A_host_can_add_label_names_and_the_plain_ones_still_work()
    {
        var rules = new TriggerRules(Map(("feature", ["Enhancement", "story"]), ("bug", ["defect"])));

        Assert.Equal("feature", rules.KindOf(Ticket(["enhancement"])));
        Assert.Equal("feature", rules.KindOf(Ticket(["STORY"])));
        Assert.Equal("bug", rules.KindOf(Ticket(["defect"])));
        Assert.Equal("feature", rules.KindOf(Ticket(["feature"])));
        Assert.Null(rules.KindOf(Ticket(["question"])));
    }

    [Fact]
    public void A_host_with_work_item_types_can_map_them_and_a_ticket_with_no_type_is_not_asked_about_it()
    {
        var rules = new TriggerRules(ItemTypes: Map(("bug", ["Bug"]), ("feature", ["User Story", "Feature"])));

        Assert.Equal("bug", rules.KindOf(Ticket([], "bug")));
        Assert.Equal("feature", rules.KindOf(Ticket([], "User Story")));
        Assert.Null(rules.KindOf(Ticket([], "Task")));
        Assert.Null(rules.KindOf(Ticket([])));
        Assert.Null(TriggerRules.Default.KindOf(Ticket([], "Bug"))); // types count only when the host lists them
    }

    private const string Secret = "0123456789abcdef0123456789abcdef";

    private static WorkerSettings Settings(string hosts) =>
        WorkerSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FACTORY_HOSTS"] = hosts }).Build());

    [Fact]
    public void Triggers_are_read_with_their_host_and_nothing_means_the_defaults()
    {
        var settings = Settings($$$"""
            [{"name":"forgejo","url":"http://forgejo:3000","triggers":{"labels":{"feature":["enhancement"]},"item_types":{"bug":["Bug"]},"command":true}},
             {"name":"plain","url":"https://x.org"}]
            """);

        var read = settings.HostNamed("forgejo").Triggers!;
        Assert.True(read.Command);
        Assert.Equal(["enhancement"], read.Labels!["feature"]);
        Assert.Equal(["Bug"], read.ItemTypes!["bug"]);
        Assert.Equal(TriggerRules.Default, settings.HostNamed("plain").Triggers);
        Assert.False(TriggerRules.Default.Command);
    }

    [Theory]
    [InlineData("""{"command":"yes"}""")]
    [InlineData("""{"unknown":1}""")]
    [InlineData("""{"labels":{"epic":["x"]}}""")]
    [InlineData("""{"labels":{"feature":"x"}}""")]
    [InlineData("""{"labels":{"feature":[""]}}""")]
    [InlineData("""{"item_types":[]}""")]
    [InlineData("""[]""")]
    public void Triggers_that_cannot_be_used_stop_the_start_and_name_the_host(string triggers)
    {
        var error = Assert.Throws<InvalidOperationException>(() => HostsSetting.Parse($$$"""[{"name":"cb","url":"https://x.org","webhook_secret":"{{{Secret}}}","triggers":{{{triggers}}}}]"""));

        Assert.Contains("host \"cb\"", error.Message);
    }

    [Fact]
    public async Task The_activity_gives_a_host_its_own_rules()
    {
        var settings = new WorkerSettings { Hosts = [new HostSettings("work", "forgejo", "https://x.org", "", "bot", new TriggerRules(Command: true)), TestHosts.One()[0]] };
        var activities = new GitHostActivities(null!, settings);

        Assert.True((await activities.ReadTriggersAsync("work")).Command);
        Assert.False((await activities.ReadTriggersAsync("forgejo")).Command);
    }

    // ---- the command and the router

    [Theory]
    [InlineData("/factory feature", "feature")]
    [InlineData("  /FACTORY   Bug  ", "bug")]
    [InlineData("hello\n/factory docs\nthanks", "docs")]
    [InlineData("/factory feature please", null)]
    [InlineData("/factory", null)]
    [InlineData("/factory epic", null)]
    [InlineData("please /factory feature", null)]
    public void The_command_is_a_line_with_only_the_word_and_a_kind_the_factory_runs(string body, string? kind) =>
        Assert.Equal(kind, FactoryCommand.Parse(body));

    private static RepoEvent Comment(string body, string[]? labels = null, bool open = true, bool pull = false) =>
        new("forgejo", "d", "issue_comment", RepoEventKind.Commented, Owner: "acme", Repo: "shop", Number: 7, IsPullRequest: pull, IsOpen: open,
            Title: "Add wishlist", Body: "As a user...", Author: "alice", Labels: labels ?? [], CommentBody: body, CommentAuthor: "bob");

    private static readonly TriggerRules Allowed = new(Command: true);

    [Fact]
    public void The_command_starts_the_asked_workflow_only_where_the_host_allows_it()
    {
        var started = EventRouter.Decide(Comment("/factory bug"), Allowed);

        Assert.Equal((RoutingAction.StartFeature, "bug", 7, "alice"), (started.Action, started.Feature!.Kind, started.Feature.IssueNumber, started.Feature.Author));
        Assert.False(started.Restart);
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Comment("/factory bug")).Action);                        // off by default
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Comment("/factory bug"), TriggerRules.Default).Action);
    }

    [Fact]
    public void The_command_does_nothing_on_a_closed_ticket_on_the_factorys_own_comment_or_on_a_pull_request()
    {
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Comment("/factory bug", open: false), Allowed).Action);
        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(Comment($"{EventRouter.BotMarker}\n/factory bug"), Allowed).Action);
        Assert.NotEqual(RoutingAction.StartFeature, EventRouter.Decide(Comment("/factory bug", pull: true), Allowed).Action);
    }

    [Fact]
    public void On_a_ticket_that_already_has_a_kind_the_command_is_an_ordinary_comment_and_other_commands_keep_working()
    {
        Assert.Equal(RoutingAction.ReplyToFeature, EventRouter.Decide(Comment("/factory bug", ["feature"]), Allowed).Action);
        Assert.Equal(RoutingAction.AskQuestion, EventRouter.Decide(Comment("/ask what is this?"), Allowed).Action);
    }

    [Fact]
    public void A_ticket_the_hosts_triggers_name_starts_a_run_through_the_router()
    {
        var rules = new TriggerRules(new Dictionary<string, IReadOnlyList<string>> { ["feature"] = ["enhancement"] });
        var ticket = new RepoEvent("forgejo", "d", "issues", RepoEventKind.TicketOpened, Owner: "acme", Repo: "shop", Number: 7, Labels: ["Enhancement"]);

        Assert.Equal(RoutingAction.Ignore, EventRouter.Decide(ticket).Action);
        var decision = EventRouter.Decide(ticket, rules);
        Assert.Equal((RoutingAction.StartFeature, "feature"), (decision.Action, decision.Feature!.Kind));
    }
}
