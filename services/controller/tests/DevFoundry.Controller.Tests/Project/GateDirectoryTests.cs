using Xunit;

namespace DevFoundry.Controller.Tests;

public sealed class GateDirectoryTests
{
    private static string Json(string gate) => $$"""{"image":"dotnet-node","gates":[{{gate}}]}""";

    [Fact]
    public void A_gate_without_cwd_runs_its_command_as_it_is()
    {
        Assert.True(ProjectConfigParser.TryParse(Json("""{"name":"test","command":["dotnet","test"]}"""), out var config, out var error), error);
        var gate = config!.Gates[0];

        Assert.Null(gate.WorkingDirectory);
        Assert.Equal(["dotnet", "test"], gate.SandboxCommand);
        Assert.Equal("dotnet test", gate.Display);
    }

    [Fact]
    public void A_gate_with_cwd_changes_into_the_folder_and_then_replaces_the_shell_with_the_command()
    {
        Assert.True(ProjectConfigParser.TryParse(Json("""{"name":"web","command":["npm","test"],"cwd":"frontend/"}"""), out var config, out var error), error);
        var gate = config!.Gates[0];

        Assert.Equal("frontend", gate.WorkingDirectory); // the trailing slash is dropped
        Assert.Equal(["sh", "-c", "cd \"$1\" && shift && exec \"$@\"", "gate", "frontend", "npm", "test"], gate.SandboxCommand);
        Assert.Equal("(in frontend) npm test", gate.Display);
    }

    [Theory]
    [InlineData("\"/etc\"")]
    [InlineData("\"../x\"")]
    [InlineData("\"a/../b\"")]
    [InlineData("\"web app\"")]
    [InlineData("\"$(id)\"")]
    [InlineData("\"\"")]
    [InlineData("3")]
    public void A_cwd_that_is_not_a_folder_inside_the_repository_is_refused(string value)
    {
        Assert.False(ProjectConfigParser.TryParse(Json($$"""{"name":"web","command":["npm","test"],"cwd":{{value}}}"""), out _, out var error));

        Assert.Contains("cwd", error);
    }

    [Fact]
    public void Developers_and_reviewers_are_told_which_folder_a_check_runs_in()
    {
        var gate = new Gate("web", ["npm", "test"], 60, "frontend");

        Assert.Equal(["web: (in frontend) npm test"], ReviewPrompt.Describe([gate]));
    }
}
