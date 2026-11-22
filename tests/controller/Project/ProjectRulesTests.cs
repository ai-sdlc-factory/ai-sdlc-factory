using Xunit;

namespace AiSdlcFactory.Controller.Tests;

public sealed class ProjectRulesTests
{
    private static string Json(string rules) => $$"""{"image":"python","gates":[{"name":"test","command":["pytest"]}],"rules":{{rules}}}""";

    private static ProjectConfig Parse(string rules)
    {
        Assert.True(ProjectConfigParser.TryParse(Json(rules), out var config, out var error), error);
        return config!;
    }

    private static string ParseError(string rules)
    {
        Assert.False(ProjectConfigParser.TryParse(Json(rules), out _, out var error));
        return error;
    }

    [Fact]
    public void A_rule_is_read_with_its_defaults()
    {
        var rule = Assert.Single(Parse("""[{"id":"no-print","files":"src/**/*.py","forbid":"\\bprint\\(","message":"Use logging."}]""").Rules!);

        Assert.Equal(("no-print", "Use logging.", true), (rule.Id, rule.Message, rule.Block));
        Assert.Equal(["src/**/*.py"], rule.Files);
    }

    [Fact]
    public void Files_may_be_several_patterns_and_severity_may_be_warn()
    {
        var rule = Assert.Single(Parse("""[{"id":"r","files":["a/*.py","*.js"],"forbid":"x","message":"m","severity":"warn"}]""").Rules!);

        Assert.Equal(2, rule.Files.Count);
        Assert.False(rule.Block);
    }

    [Fact]
    public void A_project_without_rules_has_an_empty_list()
    {
        Assert.True(ProjectConfigParser.TryParse("""{"image":"python","gates":[{"name":"test","command":["pytest"]}]}""", out var config, out _));
        Assert.Empty(config!.Rules!);
    }

    [Theory]
    [InlineData("""[{"id":"A","files":"*.py","forbid":"x","message":"m"}]""", "id")]
    [InlineData("""[{"id":"a","files":"/etc/*","forbid":"x","message":"m"}]""", "files")]
    [InlineData("""[{"id":"a","files":"*.py","message":"m"}]""", "forbid")]
    [InlineData("""[{"id":"a","files":"*.py","forbid":"(?<=x)y","message":"m"}]""", "regular expression")]
    [InlineData("""[{"id":"a","files":"*.py","forbid":"(a)\\1","message":"m"}]""", "regular expression")]
    [InlineData("""[{"id":"a","files":"*.py","forbid":"x"}]""", "message")]
    [InlineData("""[{"id":"a","files":"*.py","forbid":"x","message":"m","severity":"fatal"}]""", "severity")]
    [InlineData("""[{"id":"a","files":"*.py","forbid":"x","message":"m"},{"id":"a","files":"*.py","forbid":"y","message":"m"}]""", "unique")]
    [InlineData("""{"id":"a"}""", "array")]
    public void A_rule_that_cannot_be_used_is_refused_and_the_message_names_the_problem(string rules, string mentions) =>
        Assert.Contains(mentions, ParseError(rules));

    [Fact]
    public void Too_many_rules_are_refused()
    {
        var many = "[" + string.Join(',', Enumerable.Range(1, 21).Select(i => $$"""{"id":"r{{i}}","files":"*.py","forbid":"x","message":"m"}""")) + "]";

        Assert.Contains("at most 20", ParseError(many));
    }

    private static readonly ProjectRule NoPrint = new("no-print", ["*.py"], @"\bprint\(", "Use logging.");

    [Fact]
    public void Only_added_lines_in_matching_files_are_blamed_with_their_new_line_number()
    {
        var diff = "diff --git a/hello.py b/hello.py\n--- a/hello.py\n+++ b/hello.py\n@@ -3,2 +3,4 @@\n print('old context')\n-print('removed')\n+x = 1\n+print('new')\n y = 2\n" +
                   "diff --git a/notes.txt b/notes.txt\n--- a/notes.txt\n+++ b/notes.txt\n@@ -1 +1,2 @@\n+print('not python')\n";

        var hit = Assert.Single(ProjectRules.Check([NoPrint], diff));

        Assert.Equal(("hello.py", 5, "print('new')"), (hit.Path, hit.Line, hit.Text));
    }

    [Fact]
    public void A_pattern_anchored_to_the_start_of_the_line_works()
    {
        var rule = new ProjectRule("no-import", ["*.py"], "^import os", "No os.");
        var diff = "diff --git a/a.py b/a.py\n--- a/a.py\n+++ b/a.py\n@@ -0,0 +1,2 @@\n+import os\n+x = 'import os'\n";

        Assert.Single(ProjectRules.Check([rule], diff));
    }

    [Fact]
    public void Blocking_rules_become_developer_feedback_and_warnings_become_reviewer_notes()
    {
        var warn = new ProjectRule("no-todo", ["*.py"], "TODO", "Finish it.", Block: false);
        var diff = "diff --git a/a.py b/a.py\n--- a/a.py\n+++ b/a.py\n@@ -0,0 +1,2 @@\n+print('x')\n+# TODO later\n";

        var hits = ProjectRules.Check([NoPrint, warn], diff);

        Assert.Contains("no-print", ProjectRules.BlockingFeedback(hits));
        Assert.DoesNotContain("no-todo", ProjectRules.BlockingFeedback(hits));
        Assert.Contains("no-todo", ProjectRules.WarningNotes(hits));
        Assert.Null(ProjectRules.BlockingFeedback(hits.Where(h => !h.Rule.Block)));
        Assert.Null(ProjectRules.WarningNotes([]));
    }

    [Fact]
    public void One_rule_reports_at_most_three_lines()
    {
        var added = string.Concat(Enumerable.Range(1, 8).Select(i => $"+print({i})\n"));
        var diff = $"diff --git a/a.py b/a.py\n--- a/a.py\n+++ b/a.py\n@@ -0,0 +1,8 @@\n{added}";

        Assert.Equal(3, ProjectRules.Check([NoPrint], diff).Count);
    }

    [Fact]
    public void No_rules_means_no_work()
    {
        Assert.Empty(ProjectRules.Check([], "anything"));
    }

    [Fact]
    public void The_builders_are_told_the_blocking_rules_up_front_and_not_the_warnings()
    {
        var warn = new ProjectRule("no-todo", ["*.py"], "TODO", "Finish it.", Block: false);
        var pull = new PullRequestDetails(7, "t", "b", "open", false, "factory/issue-7", "abc", "a/b", "a/b", "main", "factory-bot");
        var comments = new List<RevisionComment> { new(1, "bob", null, null, "Do it.") };

        var prompt = RevisionPrompts.Build(pull, comments, [], null, 1, 3, [NoPrint, warn]);

        Assert.Contains("no-print (in *.py): Use logging.", prompt);
        Assert.DoesNotContain("no-todo", prompt);
        Assert.Null(ProjectRules.PromptSection([warn]));
        Assert.Null(ProjectRules.PromptSection(null));
        Assert.DoesNotContain("project's own rules", RevisionPrompts.Build(pull, comments, [], null, 1, 3));
    }
}
