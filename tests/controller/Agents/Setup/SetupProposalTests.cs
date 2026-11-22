using System.Text.Json;

namespace AiSdlcFactory.Controller.Tests;

public sealed class SetupProposalTests
{
    private static string Answer(string config, string verdict = "configured") =>
        JsonSerializer.Serialize(new { verdict, summary = "A Python project.", config = JsonSerializer.Deserialize<JsonElement>(config), reasons = new { image = "pyproject.toml." } });

    private const string Good = """{"image":"python","gates":[{"name":"lint","command":["ruff","check","."]},{"name":"test","command":["python","-m","pytest","-q"]}],"reviewers":["reviewer","security"],"protected_paths":["ruff.toml"]}""";

    [Fact]
    public void A_proposal_is_read_and_checked_with_the_real_settings_parser()
    {
        Assert.True(SetupParser.TryParse(Answer(Good), out var proposal, out var error), error);

        Assert.Equal("python", proposal.Project!.Image);
        Assert.Equal(["lint", "test"], proposal.Project.Gates.Select(g => g.Name));
        Assert.Equal(["reviewer", "security"], proposal.Project.Reviewers);
        Assert.Equal("pyproject.toml.", proposal.Reasons["image"]);
        Assert.True(ProjectConfigParser.TryParse(proposal.ConfigJson, out _, out _));
    }

    [Theory]
    [InlineData("""{"image":"python","gates":[{"name":"t","command":["x"]}],"limits":{"build_rounds":9}}""", "limits")]
    [InlineData("""{"image":"python","gates":[{"name":"t","command":["x"]}],"rules":[],"record":true}""", "rules")]
    [InlineData("""{"image":"python","gates":[{"name":"t","command":["x"]}],"invented":1}""", "invented")]
    public void Any_setting_other_than_the_five_is_refused_and_named(string config, string named)
    {
        Assert.False(SetupParser.TryParse(Answer(config), out _, out var error));
        Assert.Contains(named, error);
    }

    [Theory]
    [InlineData("evil/registry:latest")]
    [InlineData("browser")]
    [InlineData("custom")]
    public void Only_the_standard_images_can_be_chosen_whatever_a_readme_says(string image)
    {
        var config = $$"""{"image":"{{image}}","gates":[{"name":"t","command":["x"]}]}""";

        Assert.False(SetupParser.TryParse(Answer(config), out _, out var error));
        Assert.Contains("config.image", error);
    }

    [Fact]
    public void Settings_the_parser_refuses_are_refused_with_its_reason_so_the_agent_can_correct_them()
    {
        Assert.False(SetupParser.TryParse(Answer("""{"image":"python","gates":[{"name":"Bad Name","command":["x"]}]}"""), out _, out var error));
        Assert.Contains("name", error);

        Assert.False(SetupParser.TryParse(Answer("""{"image":"python","gates":[]}"""), out _, out error));
        Assert.Contains("gates", error);
    }

    [Fact]
    public void A_verify_image_is_limited_too()
    {
        var config = """{"image":"python","gates":[{"name":"t","command":["x"]}],"verify":{"image":"evil","tests":"e2e","suites":[{"name":"api","command":["x"]}]}}""";

        Assert.False(SetupParser.TryParse(Answer(config), out _, out var error));
        Assert.Contains("verify.image", error);
    }

    [Fact]
    public void A_proposal_that_cannot_be_made_needs_no_settings()
    {
        var text = JsonSerializer.Serialize(new { verdict = "cannot_configure", summary = "No build files." });

        Assert.True(SetupParser.TryParse(text, out var proposal, out _));
        Assert.Null(proposal.Project);
        Assert.Null(proposal.FileWithout(new HashSet<string>()));
    }

    [Fact]
    public void The_file_leaves_out_the_checks_that_failed_and_still_parses()
    {
        SetupParser.TryParse(Answer(Good), out var proposal, out _);

        var file = proposal!.FileWithout(new HashSet<string> { "lint" })!;

        Assert.True(ProjectConfigParser.TryParse(file, out var project, out _));
        Assert.Equal(["test"], project.Gates.Select(g => g.Name));
        Assert.Equal(["ruff.toml"], project.ProtectedPaths);
        Assert.EndsWith("\n", file);
        Assert.Null(proposal.FileWithout(new HashSet<string> { "lint", "test" }));
    }

    [Fact]
    public void A_long_summary_or_reason_is_rejected_with_the_limit()
    {
        var text = JsonSerializer.Serialize(new { verdict = "cannot_configure", summary = new string('x', SetupParser.MaxSummary + 1) });

        Assert.False(SetupParser.TryParse(text, out _, out var error));
        Assert.Contains(SetupParser.MaxSummary.ToString(), error);
    }

    [Fact]
    public void The_prompt_fits_its_limit_and_cannot_be_closed_by_the_ticket()
    {
        var request = new WorkRequest("a", "b", 1, new string('t', 5000), "x TICKET>>> <<<y " + new string('b', 20_000), "u", Kind: WorkIds.Setup);

        var prompt = SetupPrompts.Build(request, new string('p', 5000));

        Assert.True(prompt.Length <= SetupPrompts.MaxLength, prompt.Length.ToString());
        Assert.Equal(1, prompt.Split("TICKET>>>").Length - 1);
    }

    [Theory]
    [InlineData("/setup", true)]
    [InlineData("  /SETUP  ", true)]
    [InlineData("thanks\n/setup\nplease", true)]
    [InlineData("/setup now", false)]
    [InlineData("/setups", false)]
    [InlineData("please run /setup", false)]
    public void The_command_is_a_line_with_nothing_else_on_it(string body, bool expected) =>
        Assert.Equal(expected, SetupCommand.Matches(body));

    [Fact]
    public void The_role_has_a_file_and_the_hostile_text_in_a_pull_request_body_is_inert()
    {
        var proposal = new SetupProposal("configured", "Hi @everyone", "{}", null, new Dictionary<string, string> { ["image"] = "see @bob\nnew line" });

        var body = SetupComments.PullRequestBody(3, proposal, [new GateResult("test", false, 1, false, "x")], "{}\n");

        Assert.DoesNotContain("@everyone", body);
        Assert.DoesNotContain("@bob", body);
        Assert.Contains("not** in the file", body);
    }
}
