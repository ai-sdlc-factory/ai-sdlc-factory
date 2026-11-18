namespace DevFoundry.Controller.Tests;

public sealed class MissingAgentRoleTests
{
    [Fact]
    public void Opencodes_warning_for_the_role_is_recognised()
    {
        const string stderr = "!  agent \"planner\" not found. Falling back to default agent\n";

        Assert.True(MissingAgentRole.IsReported(stderr, "planner"));
    }

    [Theory]
    [InlineData("!  agent \"planner\" not found. Falling back to default agent", "asker")]
    [InlineData("", "planner")]
    [InlineData(null, "planner")]
    [InlineData("some other warning about the planner", "planner")]
    public void Other_output_is_not_mistaken_for_a_missing_role(string? stderr, string role) =>
        Assert.False(MissingAgentRole.IsReported(stderr, role));
}
