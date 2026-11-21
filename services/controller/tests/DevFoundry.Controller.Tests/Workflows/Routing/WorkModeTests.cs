namespace DevFoundry.Controller.Tests;

public sealed class WorkModeTests
{
    private static ProjectConfig Project(bool quickByDefault = false) =>
        new("python", [new Gate("test", ["pytest"], 60)], ["reviewer"], QuickByDefault: quickByDefault);

    [Fact]
    public void A_ticket_without_labels_runs_the_full_process() =>
        Assert.Null(WorkMode.QuickReason(["feature"], Project()));

    [Theory]
    [InlineData("quick")]
    [InlineData("Quick")]
    [InlineData("QUICK")]
    public void The_quick_label_selects_quick_mode_whatever_its_case(string label)
    {
        var reason = WorkMode.QuickReason(["feature", label], Project());

        Assert.Equal("the ticket has the \"quick\" label", reason);
    }

    [Fact]
    public void A_project_can_make_quick_the_default() =>
        Assert.Equal("the project's default mode is quick", WorkMode.QuickReason(["feature"], Project(quickByDefault: true)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_full_label_always_wins(bool projectDefault)
    {
        Assert.Null(WorkMode.QuickReason(["feature", "full"], Project(projectDefault)));
        Assert.Null(WorkMode.QuickReason(["quick", "full"], Project(projectDefault))); // contradictory labels get the safer mode
    }

    [Fact]
    public void A_ticket_with_no_label_list_is_handled() =>
        Assert.Null(WorkMode.QuickReason(null, Project()));
}

public sealed class ProjectModeSettingTests
{
    private static string Config(string extra) =>
        """{"image":"python","gates":[{"name":"test","command":["pytest"]}]""" + extra + "}";

    [Fact]
    public void A_project_is_full_unless_it_says_otherwise()
    {
        Assert.True(ProjectConfigParser.TryParse(Config(""), out var config, out _));
        Assert.False(config.QuickByDefault);
    }

    [Theory]
    [InlineData(",\"mode\":\"quick\"", true)]
    [InlineData(",\"mode\":\"full\"", false)]
    public void The_project_can_choose_its_default_mode(string extra, bool quick)
    {
        Assert.True(ProjectConfigParser.TryParse(Config(extra), out var config, out var error), error);
        Assert.Equal(quick, config.QuickByDefault);
    }

    [Theory]
    [InlineData(",\"mode\":\"fast\"")]
    [InlineData(",\"mode\":true")]
    [InlineData(",\"mode\":\"Quick\"")]
    [InlineData(",\"mode\":null")]
    public void Any_other_value_is_refused_with_a_message(string extra)
    {
        Assert.False(ProjectConfigParser.TryParse(Config(extra), out _, out var error));
        Assert.Contains("\"mode\" must be \"full\"", error);
    }
}

public sealed class QuickModeTextTests
{
    [Fact]
    public void The_ticket_comment_says_what_is_skipped_why_and_how_to_get_the_full_process()
    {
        var text = TicketComments.QuickMode("the ticket has the \"quick\" label", []);

        Assert.Contains("Because the ticket has the \"quick\" label, I skip the coverage check, the design review and the acceptance check", text);
        Assert.Contains("the project's checks and the reviewers still run", text);
        Assert.Contains("add the `full` label", text);
        Assert.DoesNotContain("Triage would have asked", text);
    }

    [Fact]
    public void A_design_review_that_triage_would_have_asked_for_is_mentioned()
    {
        var text = TicketComments.QuickMode("the ticket has the \"quick\" label", ["a public API changes", "a new dependency is needed"]);

        Assert.Contains("Triage would have asked for a design review (a public API changes; a new dependency is needed)", text);
    }

    [Fact]
    public void The_pull_request_says_the_stages_were_skipped_only_in_quick_mode()
    {
        var request = new WorkRequest("acme", "shop", 7, "Add it", "Body", "alice");
        var report = new DeveloperReport("done", "Added it.", []);

        var quick = PullRequestText.Body(request, report, [], null, [], 1, quickReason: "the ticket has the \"quick\" label");
        var full = PullRequestText.Body(request, report, [], null, [], 1);

        Assert.Contains("## Quick mode", quick);
        Assert.Contains("the coverage check, the design review and the acceptance check were skipped", quick);
        Assert.DoesNotContain("Quick mode", full);
    }
}
