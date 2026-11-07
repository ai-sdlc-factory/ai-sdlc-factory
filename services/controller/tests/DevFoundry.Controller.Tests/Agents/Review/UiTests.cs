using System.Text;

namespace DevFoundry.Controller.Tests;

public sealed class GlobTests
{
    [Theory]
    [InlineData("web/**", "web/index.html", true)]
    [InlineData("web/**", "web/css/site.css", true)]
    [InlineData("web/**", "webapp/index.html", false)]
    [InlineData("web/**", "src/web/index.html", false)]
    [InlineData("web/*", "web/index.html", true)]
    [InlineData("web/*", "web/css/site.css", false)]
    [InlineData("**/*.tsx", "Button.tsx", true)]
    [InlineData("**/*.tsx", "src/ui/Button.tsx", true)]
    [InlineData("src/**/*.vue", "src/App.vue", true)]
    [InlineData("src/**/*.vue", "src/a/b/App.vue", true)]
    [InlineData("src/**/*.vue", "lib/App.vue", false)]
    [InlineData("*.css", "site.css", true)]
    [InlineData("*.css", "deep/folder/site.css", true)]
    [InlineData("*.css", "site.scss", false)]
    [InlineData("*.css", "site.css.map", false)]
    [InlineData("templates/?.html", "templates/a.html", true)]
    [InlineData("templates/?.html", "templates/ab.html", false)]
    [InlineData("static/app.js", "static/app.js", true)]
    [InlineData("static/app.js", "static/appxjs", false)]
    [InlineData("static/app.js", "other/static/app.js", false)]
    public void Patterns_match_paths_as_documented(string pattern, string path, bool expected) =>
        Assert.Equal(expected, Globs.IsMatch(path, pattern));

    [Fact]
    public void Regex_characters_in_a_pattern_are_taken_literally()
    {
        Assert.True(Globs.IsMatch("a+b/(c).html", "a+b/(c).html"));
        Assert.False(Globs.IsMatch("aab/c.html", "a+b/c.html"));
        Assert.False(Globs.IsMatch("xhtml", "*.html"));
    }

    [Theory]
    [InlineData("web/**", true)]
    [InlineData("*.css", true)]
    [InlineData("src/**/*.tsx", true)]
    [InlineData("", false)]
    [InlineData("/etc/**", false)]
    [InlineData("../x/**", false)]
    [InlineData("a b/**", false)]
    [InlineData("a;rm/**", false)]
    public void Only_plain_relative_patterns_are_valid(string pattern, bool valid) =>
        Assert.Equal(valid, Globs.IsValidPattern(pattern));
}

public sealed class UnifiedDiffTests
{
    private const string Tracked = """
        diff --git a/hello.py b/hello.py
        index d9895ff..ddfffdb 100644
        --- a/hello.py
        +++ b/hello.py
        @@ -6,8 +6,12 @@ def greeting() -> str:
         
        +def goodbye() -> str:
        +    return "Goodbye, world!"
        -    print(greeting())
        +    print(goodbye())
        diff --git a/web/site.css b/web/site.css
        --- a/web/site.css
        +++ b/web/site.css
        @@ -1,2 +1,2 @@
        -a { color: red; }
        +a { color: blue; }
        """;

    private static string New(string path, int lines) =>
        $"diff --git a/{path} b/{path}\nnew file\n--- /dev/null\n+++ b/{path}\n" + string.Join("\n", Enumerable.Range(1, lines).Select(i => $"+line {i}"));

    [Fact]
    public void A_diff_is_split_per_file_with_changed_line_counts()
    {
        var files = UnifiedDiff.Split(Tracked);

        Assert.Equal(["hello.py", "web/site.css"], files.Select(f => f.Path));
        Assert.Equal(4, files[0].Changed); // 3 added, 1 removed; the --- and +++ headers do not count
        Assert.Equal(2, files[1].Changed);
        Assert.StartsWith("diff --git a/hello.py", files[0].Text);
        Assert.DoesNotContain("web/site.css", files[0].Text);
    }

    [Fact]
    public void New_files_without_hunk_markers_are_counted_whole()
    {
        var files = UnifiedDiff.Split(New("web/new.html", 12));
        Assert.Equal(12, Assert.Single(files).Changed);
    }

    [Fact]
    public void Large_new_files_that_are_not_shown_still_count_by_their_length()
    {
        var files = UnifiedDiff.Split("diff --git a/web/big.css b/web/big.css\nnew file (large: 1523 lines, not shown)");
        Assert.Equal(1523, Assert.Single(files).Changed);
    }

    [Fact]
    public void Binary_files_count_as_files_but_add_no_lines()
    {
        var files = UnifiedDiff.Split("diff --git a/web/logo.png b/web/logo.png\nnew file (binary, not shown)");
        Assert.Equal(0, Assert.Single(files).Changed);
    }

    [Fact]
    public void Only_keeps_the_named_files_and_nothing_else()
    {
        var only = UnifiedDiff.Only(Tracked, ["web/site.css"]);

        Assert.Contains("color: blue", only);
        Assert.DoesNotContain("goodbye", only);
        Assert.Equal("", UnifiedDiff.Only(Tracked, ["missing.txt"]));
    }

    [Fact]
    public void An_empty_diff_has_no_files() => Assert.Empty(UnifiedDiff.Split(""));

    [Fact]
    public void A_content_line_that_looks_like_a_header_is_not_mistaken_for_a_file()
    {
        var diff = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-old\n+diff --git a/x b/x";
        // The added line text is "diff --git a/x b/x" with a leading +, so it does not start a new file.
        Assert.Single(UnifiedDiff.Split(diff));
    }
}

public sealed class UiChangeTests
{
    private static readonly UiConfig Ui = new(["web/**", "*.css"], UiConfig.DefaultMedium, UiConfig.DefaultLarge);

    private static FileChange Added(string path) => new(path, FileChange.Added);

    private static string Diff(params (string Path, int Lines)[] files) =>
        string.Join("\n", files.Select(f => $"diff --git a/{f.Path} b/{f.Path}\nnew file\n--- /dev/null\n+++ b/{f.Path}\n" +
            string.Join("\n", Enumerable.Range(1, f.Lines).Select(i => $"+line {i}"))));

    private static UiChange Classify(UiConfig ui, params (string Path, int Lines)[] files) =>
        UiChange.Classify(files.Select(f => Added(f.Path)), Diff(files), ui);

    [Fact]
    public void No_UI_file_means_no_UI_change_whatever_else_changed()
    {
        var change = Classify(Ui, ("hello.py", 500), ("src/app.py", 500));

        Assert.Equal(UiSize.None, change.Size);
        Assert.False(change.ShouldReview);
        Assert.Empty(change.Files);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(1, 39)]
    [InlineData(2, 20)]
    public void A_few_files_and_lines_is_a_small_change_that_is_not_reviewed(int files, int linesPerFile)
    {
        var change = Classify(Ui, Enumerable.Range(1, files).Select(i => ($"web/f{i}.html", linesPerFile)).ToArray());

        Assert.Equal(files * linesPerFile >= 40 ? UiSize.Medium : UiSize.Small, change.Size);
    }

    [Fact]
    public void Small_means_fewer_than_the_medium_numbers_on_both_counts()
    {
        var change = Classify(Ui, ("web/a.html", 10), ("web/b.css", 10));

        Assert.Equal(UiSize.Small, change.Size);
        Assert.False(change.ShouldReview);
        Assert.Equal(20, change.Lines);
    }

    [Fact]
    public void Three_files_is_medium_however_short()
    {
        var change = Classify(Ui, ("web/a.html", 1), ("web/b.html", 1), ("web/c.html", 1));

        Assert.Equal(UiSize.Medium, change.Size);
        Assert.True(change.ShouldReview);
    }

    [Fact]
    public void Forty_lines_is_medium_even_in_one_file() =>
        Assert.Equal(UiSize.Medium, Classify(Ui, ("web/a.html", 40)).Size);

    [Fact]
    public void Eight_files_or_two_hundred_lines_is_large()
    {
        Assert.Equal(UiSize.Large, Classify(Ui, Enumerable.Range(1, 8).Select(i => ($"web/f{i}.html", 1)).ToArray()).Size);
        Assert.Equal(UiSize.Large, Classify(Ui, ("web/a.html", 200)).Size);
        Assert.True(Classify(Ui, ("web/a.html", 200)).ShouldReview);
    }

    [Fact]
    public void Only_lines_in_UI_files_count()
    {
        var change = Classify(Ui, ("web/a.html", 10), ("src/service.py", 900));

        Assert.Equal(UiSize.Small, change.Size);
        Assert.Equal(10, change.Lines);
        Assert.Equal(["web/a.html"], change.Files);
    }

    [Fact]
    public void Modified_and_deleted_files_count_their_changed_lines()
    {
        var diff = "diff --git a/web/a.html b/web/a.html\n--- a/web/a.html\n+++ b/web/a.html\n@@ -1,30 +1,30 @@\n" +
                   string.Join("\n", Enumerable.Range(1, 25).Select(i => $"-old {i}")) + "\n" +
                   string.Join("\n", Enumerable.Range(1, 25).Select(i => $"+new {i}"));

        var change = UiChange.Classify([new FileChange("web/a.html", FileChange.Modified)], diff, Ui);

        Assert.Equal(50, change.Lines);
        Assert.Equal(UiSize.Medium, change.Size);
    }

    [Fact]
    public void A_diff_that_hit_the_cap_is_large_because_its_size_cannot_be_trusted()
    {
        var capped = Diff(("web/a.html", 3)) + new string('x', UnifiedDiff.MaxChars);

        var change = UiChange.Classify([Added("web/a.html")], capped, Ui);

        Assert.Equal(UiSize.Large, change.Size);
    }

    [Fact]
    public void A_large_file_that_was_not_shown_still_counts_by_its_length()
    {
        var diff = "diff --git a/site.css b/site.css\nnew file (large: 900 lines, not shown)";

        Assert.Equal(UiSize.Large, UiChange.Classify([Added("site.css")], diff, Ui).Size);
    }

    [Fact]
    public void The_projects_own_thresholds_decide()
    {
        var strict = new UiConfig(["web/**"], new UiThreshold(2, 5), new UiThreshold(4, 10));

        Assert.Equal(UiSize.Small, Classify(strict, ("web/a.html", 4)).Size);
        Assert.Equal(UiSize.Medium, Classify(strict, ("web/a.html", 5)).Size);
        Assert.Equal(UiSize.Large, Classify(strict, ("web/a.html", 10)).Size);
        Assert.Equal(UiSize.Medium, Classify(strict, ("web/a.html", 1), ("web/b.html", 1)).Size); // two files is enough
        Assert.Equal(UiSize.Large, Classify(strict, ("web/a.html", 1), ("web/b.html", 1), ("web/c.html", 1), ("web/d.html", 1)).Size);
    }

    [Fact]
    public void The_same_change_always_gets_the_same_answer()
    {
        var first = Classify(Ui, ("web/a.html", 30), ("web/b.css", 30));
        var second = Classify(Ui, ("web/a.html", 30), ("web/b.css", 30));
        Assert.Equal((first.Size, first.Lines), (second.Size, second.Lines));
    }
}

public sealed class ProjectUiConfigTests
{
    private static string Config(string extra) =>
        "{\"image\":\"python\",\"gates\":[{\"name\":\"t\",\"command\":[\"x\"]}]" + extra + "}";

    [Fact]
    public void A_project_without_ui_has_none()
    {
        Assert.True(ProjectConfigParser.TryParse(Config(""), out var config, out _));
        Assert.Null(config!.Ui);
    }

    [Fact]
    public void Paths_alone_use_the_default_thresholds()
    {
        Assert.True(ProjectConfigParser.TryParse(Config(",\"ui\":{\"paths\":[\"web/**\",\"*.css\"]}"), out var config, out var error), error);

        Assert.Equal(["web/**", "*.css"], config!.Ui!.Paths);
        Assert.Equal(new UiThreshold(3, 40), config.Ui.Medium);
        Assert.Equal(new UiThreshold(8, 200), config.Ui.Large);
    }

    [Fact]
    public void Thresholds_can_be_set_by_the_project()
    {
        Assert.True(ProjectConfigParser.TryParse(
            Config(",\"ui\":{\"paths\":[\"web/**\"],\"medium\":{\"files\":2,\"lines\":10},\"large\":{\"files\":5,\"lines\":100}}"), out var config, out var error), error);

        Assert.Equal(new UiThreshold(2, 10), config!.Ui!.Medium);
        Assert.Equal(new UiThreshold(5, 100), config.Ui.Large);
    }

    [Theory]
    [InlineData(",\"ui\":{}")]
    [InlineData(",\"ui\":[]")]
    [InlineData(",\"ui\":{\"paths\":[]}")]
    [InlineData(",\"ui\":{\"paths\":\"web/**\"}")]
    [InlineData(",\"ui\":{\"paths\":[\"/etc/**\"]}")]
    [InlineData(",\"ui\":{\"paths\":[\"../x/**\"]}")]
    [InlineData(",\"ui\":{\"paths\":[\"a b\"]}")]
    [InlineData(",\"ui\":{\"paths\":[\"web/**\"],\"medium\":{\"files\":0,\"lines\":10}}")]
    [InlineData(",\"ui\":{\"paths\":[\"web/**\"],\"medium\":{\"files\":3}}")]
    [InlineData(",\"ui\":{\"paths\":[\"web/**\"],\"medium\":{\"files\":3,\"lines\":\"40\"}}")]
    [InlineData(",\"ui\":{\"paths\":[\"web/**\"],\"medium\":{\"files\":9,\"lines\":10},\"large\":{\"files\":5,\"lines\":100}}")]
    public void Unusable_ui_settings_are_rejected_with_a_reason(string extra)
    {
        Assert.False(ProjectConfigParser.TryParse(Config(extra), out _, out var error));
        Assert.Contains("ui", error);
    }

    [Fact]
    public void More_than_twenty_patterns_are_refused()
    {
        var many = string.Join(",", Enumerable.Range(1, 21).Select(i => $"\"d{i}/**\""));
        Assert.False(ProjectConfigParser.TryParse(Config($",\"ui\":{{\"paths\":[{many}]}}"), out _, out _));
    }
}

public sealed class UxPromptTests
{
    [Fact]
    public void The_ux_prompt_lists_only_ui_files_and_says_how_many_others_exist()
    {
        var request = new WorkRequest("a", "b", 7, "Add a page", "body", "u");
        var changes = new ChangeReport([new FileChange("web/index.html", FileChange.Added)], []);

        var prompt = ReviewPrompt.Build(request, [], null, changes, "diff --git a/web/index.html b/web/index.html\n+<h1>Hi</h1>", "test passed", null, otherFilesNotShown: 4);

        Assert.Contains("- added: web/index.html", prompt);
        Assert.Contains("and 4 other changed file(s) that are not part of this review", prompt);
        Assert.Contains("+<h1>Hi</h1>", prompt);
    }

    [Fact]
    public void Without_other_files_the_note_is_left_out()
    {
        var request = new WorkRequest("a", "b", 7, "Add a page", "body", "u");

        var prompt = ReviewPrompt.Build(request, [], null, new ChangeReport([], []), "", "test passed", null);

        Assert.DoesNotContain("not part of this review", prompt);
    }

    [Fact]
    public void The_pull_request_says_why_the_ux_review_was_skipped()
    {
        var body = PullRequestText.Body(
            new WorkRequest("a", "b", 7, "T", "b", "u"), new DeveloperReport("done", "Did it.", []), [], null,
            [new GateResult("test", true, 0, false, "")], 1, [], null, "the UI change is small (1 UI file(s), 12 changed line(s))");

        Assert.Contains("## Review", body);
        Assert.Contains("- **ux**: not reviewed, the UI change is small (1 UI file(s), 12 changed line(s))", body);
    }
}
