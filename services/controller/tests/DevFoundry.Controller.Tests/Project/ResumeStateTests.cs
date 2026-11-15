namespace DevFoundry.Controller.Tests;

public sealed class ResumeStateTests
{
    private static WorkRequest Ticket(string title = "Add goodbye", string body = "Add a goodbye greeting.", string[]? labels = null, string kind = WorkIds.Feature) =>
        new("acme", "shop", 7, title, body, "alice", labels ?? ["feature"], kind);

    private static WorkspaceInfo Workspace(string head = "abc123") => new("feature-acme-shop-7-x", "/work/x", "factory/issue-7", "main", head);

    [Fact]
    public void The_fingerprint_does_not_depend_on_the_order_or_case_of_labels_or_on_who_wrote_the_ticket()
    {
        var one = ResumeState.FingerprintOf(Ticket(labels: ["feature", "Quick"]));
        var other = ResumeState.FingerprintOf(Ticket(labels: ["quick", "FEATURE"]) with { Author = "carol", IssueNumber = 7 });

        Assert.Equal(one, other);
    }

    [Theory]
    [InlineData("Add hello", "Add a goodbye greeting.", "feature", WorkIds.Feature)]
    [InlineData("Add goodbye", "Add a goodbye greeting, politely.", "feature", WorkIds.Feature)]
    [InlineData("Add goodbye", "Add a goodbye greeting.", "quick", WorkIds.Feature)]
    [InlineData("Add goodbye", "Add a goodbye greeting.", "feature", WorkIds.Bug)]
    public void Anything_the_stages_read_from_the_ticket_changes_the_fingerprint(string title, string body, string label, string kind)
    {
        var original = ResumeState.FingerprintOf(Ticket());

        Assert.NotEqual(original, ResumeState.FingerprintOf(Ticket(title, body, [label], kind)));
    }

    [Fact]
    public void A_record_is_used_only_for_the_same_ticket_on_the_same_default_branch_tip()
    {
        var state = ResumeState.Start(Ticket(), Workspace());

        Assert.Null(state.WhyNot(Ticket(), Workspace()));
        Assert.Contains("ticket's text or labels changed", state.WhyNot(Ticket(body: "Something else."), Workspace()));
        Assert.Contains("default branch has new commits", state.WhyNot(Ticket(), Workspace("def456")));
        Assert.Contains("another version", (state with { Version = 0 }).WhyNot(Ticket(), Workspace()));
    }

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("docs/setup.md", true)]
    [InlineData("CHANGELOG.MD", true)]
    [InlineData("AGENTS.md", false)]
    [InlineData("docs/agents.md", false)] // the same name as AGENTS.md, so treated as it: when in doubt, start over
    [InlineData("sub/AGENTS.md", false)]
    [InlineData("CLAUDE.md", false)]
    [InlineData(".factory/notes.md", false)]
    [InlineData(".factory/project.json", false)]
    [InlineData("src/app.py", false)]
    [InlineData("package.json", false)]
    public void Only_a_document_the_stages_do_not_read_is_an_unrelated_change(string path, bool unrelated) =>
        Assert.Equal(unrelated, ResumeState.IsUnrelated(path));

    [Fact]
    public void New_commits_that_only_changed_unrelated_documents_do_not_stale_the_record()
    {
        var state = ResumeState.Start(Ticket(), Workspace());

        Assert.Null(state.WhyNot(Ticket(), Workspace("def456"), ["README.md", "docs/a.md"]));
        Assert.Contains("default branch has new commits", state.WhyNot(Ticket(), Workspace("def456"), ["README.md", "src/a.py"]));
        Assert.Contains("default branch has new commits", state.WhyNot(Ticket(), Workspace("def456"), null));
        Assert.Contains("default branch has new commits", state.WhyNot(Ticket(), Workspace("def456"), []));
    }

    [Fact]
    public void A_record_says_which_stages_it_keeps()
    {
        var empty = ResumeState.Start(Ticket(), Workspace());
        var analysed = empty with { Analysis = new AnalystVerdict("pass", "s", ["c"], [], new Complexity(1, false, false, false, false)) };

        Assert.False(empty.HasResults);
        Assert.Empty(empty.Kept);
        Assert.True(analysed.HasResults);
        Assert.Equal(["the analysis"], analysed.Kept);
        Assert.Equal(["the analysis", "the design"], (analysed with { Design = new ArchitectDesign("pass", "s", "a", [], [], [], []) }).Kept);
    }
}
