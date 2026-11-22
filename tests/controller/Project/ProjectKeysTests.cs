using System.Text.Json;

namespace AiSdlcFactory.Controller.Tests;

/// <summary>A key in .factory/project.json that the factory does not know is reported, not silently ignored; the file still works.</summary>
public sealed class ProjectKeysTests
{
    private const string Minimal = """{"image":"python","gates":[{"name":"test","command":["pytest"]}]""";

    private static ProjectConfig Parse(string json)
    {
        Assert.True(ProjectConfigParser.TryParse(json, out var config, out var error), error);
        return config;
    }

    [Fact]
    public void A_file_with_only_known_keys_has_no_warnings() =>
        Assert.Empty(Parse(Minimal + "}").Warnings!);

    [Fact]
    public void Every_key_the_factory_reads_is_known_in_every_scope()
    {
        var json = """
        {
          "image": "browser", "reviewers": ["reviewer", "security"], "record": true, "mode": "quick",
          "gates": [{"name": "test", "command": ["npm", "test"], "timeout_seconds": 300, "cwd": "web"}],
          "ui": {"paths": ["web/**"], "medium": {"files": 3, "lines": 50}, "large": {"files": 8, "lines": 200}},
          "rules": [{"id": "no-print", "files": "src/**", "forbid": "print\\(", "message": "no print", "severity": "block"}],
          "limits": {"build_rounds": 2, "clarification_rounds": 1, "revisions_per_pull_request": 2},
          "protected_paths": ["docs/**"],
          "verify": {"start": ["npm", "start"], "start_cwd": "web", "ready": "http://127.0.0.1:5080/health", "startup_timeout_seconds": 60,
                     "tests": "e2e", "image": "browser", "suites": [{"name": "api", "command": ["npm", "run", "e2e"], "timeout_seconds": 300, "cwd": "e2e"}]}
        }
        """;

        Assert.Empty(Parse(json).Warnings!);
    }

    [Theory]
    [InlineData("revewers", "reviewers")]
    [InlineData("protected_path", "protected_paths")]
    [InlineData("Gates", "gates")]
    [InlineData("reccord", "record")]
    public void A_misspelled_key_is_named_with_the_one_that_was_probably_meant(string typo, string meant)
    {
        var config = Parse(Minimal + $", \"{typo}\": true}}");

        var warning = Assert.Single(config.Warnings!);
        Assert.Equal($"`{typo}` is not a setting the factory knows, so it is ignored. Did you mean `{meant}`?", warning);
    }

    [Fact]
    public void A_key_that_looks_like_nothing_known_is_reported_without_a_guess()
    {
        var warning = Assert.Single(Parse(Minimal + ", \"deploy_target\": \"prod\"}").Warnings!);

        Assert.Equal("`deploy_target` is not a setting the factory knows, so it is ignored", warning);
    }

    [Fact]
    public void Misspelled_keys_inside_gates_rules_ui_and_verify_are_found_with_where_they_are()
    {
        var json = """
        {
          "image": "python",
          "gates": [{"name": "test", "command": ["pytest"], "timeout": 60}],
          "ui": {"paths": ["web/**"], "medium": {"files": 3, "lines": 50, "line": 9}, "huge": {"files": 1, "lines": 1}},
          "rules": [{"id": "r", "files": "src/**", "forbid": "x", "message": "m", "severty": "warn"}],
          "verify": {"tests": "e2e", "suites": [{"name": "api", "command": ["npm", "run", "e2e"], "cmd": ["x"]}], "redy": "http://127.0.0.1:1/"}
        }
        """;
        // "redy" is read as an unknown key, but verify.ready is missing only if start is set, so the file is fine.

        var warnings = Parse(json).Warnings!;

        Assert.Contains("`timeout` in `gates[]` is not a setting the factory knows, so it is ignored. Did you mean `timeout_seconds`?", warnings);
        Assert.Contains("`line` in `ui.medium` is not a setting the factory knows, so it is ignored. Did you mean `lines`?", warnings);
        Assert.Contains(warnings, w => w.StartsWith("`huge` in `ui`"));
        Assert.Contains("`severty` in `rules[]` is not a setting the factory knows, so it is ignored. Did you mean `severity`?", warnings);
        Assert.Contains(warnings, w => w.StartsWith("`cmd` in `verify.suites[]`"));
        Assert.Contains("`redy` in `verify` is not a setting the factory knows, so it is ignored. Did you mean `ready`?", warnings);
        Assert.Equal(6, warnings.Count);
    }

    [Fact]
    public void A_file_with_an_unknown_key_still_works_and_keeps_every_known_setting()
    {
        var config = Parse(Minimal + ", \"reviewers\": [\"security\"], \"revewers\": [\"reviewer\"]}");

        Assert.Equal(["security"], config.Reviewers);
        Assert.Single(config.Warnings!);
    }

    [Fact]
    public void Text_from_the_repository_cannot_break_the_comment()
    {
        var key = "x`y\u0007" + new string('z', 100);
        var warning = Assert.Single(Parse(Minimal + $", {JsonSerializer.Serialize(key)}: 1}}").Warnings!);

        Assert.DoesNotContain("\u0007", warning);
        Assert.Equal(1, warning.Count(c => c == '`') / 2); // the key is the only code span: its own backtick was dropped
        Assert.Contains("...", warning);
    }

    [Fact]
    public void Many_unknown_keys_are_cut_off_at_ten()
    {
        var extra = string.Join(", ", Enumerable.Range(1, 25).Select(i => $"\"surplus_{i}\": 1"));

        Assert.Equal(10, Parse(Minimal + ", " + extra + "}").Warnings!.Count);
    }

    [Fact]
    public void A_scope_that_is_not_an_object_is_left_to_its_own_error_and_does_not_crash_the_check()
    {
        using var document = JsonDocument.Parse("""{"image":"x","gates":[1,"a",null],"ui":5,"rules":{"a":1},"verify":[]}""");

        Assert.Empty(ProjectKeys.Warnings(document.RootElement));
    }

    [Fact]
    public void The_notice_lists_each_warning_and_says_the_run_goes_ahead()
    {
        var text = TicketComments.UnknownProjectKeys(["`revewers` is not a setting the factory knows, so it is ignored. Did you mean `reviewers`?"]);

        Assert.Contains("a setting I do not know", text);
        Assert.Contains("- `revewers`", text);
        Assert.Contains("The run goes ahead", text);
        Assert.Contains("2 settings I do not know", TicketComments.UnknownProjectKeys(["a", "b"]));
    }
}
