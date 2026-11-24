namespace AiSdlcFactory.Controller.Tests;

/// <summary>The command that checks a project.json before any ticket arrives: usable, not usable (with the reader's own message), or unreadable.</summary>
public sealed class ProjectCheckCommandTests
{
    private const string Gates = "\"gates\":[{\"name\":\"t\",\"command\":[\"x\"]}]";

    private static (int Code, string Output) Run(string json, params string[] more)
    {
        var output = new StringWriter();
        var code = ProjectCheckCommand.Run(more.Length > 0 ? more : ["-"], new StringReader(json), output);
        return (code, output.ToString());
    }

    [Fact]
    public void A_usable_file_exits_with_0()
    {
        var (code, output) = Run("{\"image\":\"dotnet-node\"," + Gates + ",\"limits\":{\"token_budget\":20000,\"confirm_over\":10000}}");

        Assert.Equal(0, code);
        Assert.Contains("is usable", output);
    }

    [Fact]
    public void A_limit_out_of_range_exits_with_1_and_says_which_one()
    {
        var (code, output) = Run("{\"image\":\"dotnet-node\"," + Gates + ",\"limits\":{\"confirm_over\":1000}}");

        Assert.Equal(1, code);
        Assert.Contains("limits.confirm_over", output);
        Assert.Contains("10,000", output);
    }

    [Fact]
    public void Text_that_is_not_json_exits_with_1()
    {
        var (code, output) = Run("not json");

        Assert.Equal(1, code);
        Assert.Contains("not valid JSON", output);
    }

    [Fact]
    public void A_file_on_disk_is_read_and_a_missing_one_exits_with_2()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"image\":\"dotnet-node\"," + Gates + "}");
        try
        {
            Assert.Equal(0, Run("", path).Code);
        }
        finally
        {
            File.Delete(path);
        }

        var (code, output) = Run("", path);
        Assert.Equal(2, code);
        Assert.Contains("Cannot read", output);
    }

    [Fact]
    public void Without_exactly_one_argument_it_says_how_to_use_it()
    {
        var output = new StringWriter();

        Assert.Equal(2, ProjectCheckCommand.Run([], TextReader.Null, output));
        Assert.Contains("Usage: validate-project", output.ToString());
    }
}
