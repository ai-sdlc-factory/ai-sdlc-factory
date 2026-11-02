using Temporalio.Exceptions;

namespace DevFoundry.SandboxLauncher.Tests;

public sealed class SandboxPolicyTests
{
    private static ApplicationFailureException Rejected(SandboxRunRequest request, bool exists = true)
    {
        var error = Assert.Throws<ApplicationFailureException>(
            () => SandboxPolicy.Validate(request, Support.Settings(), _ => exists));
        Assert.True(error.NonRetryable);
        return error;
    }

    [Fact]
    public void Valid_request_resolves_image_and_workspace_path()
    {
        var run = Support.Valid(Support.Request(writable: true, timeout: 90));

        Assert.Equal("dev-foundry/sandbox-dotnet:local", run.Image);
        Assert.Equal("/work/feature-acme-shop-1", run.WorkspacePath);
        Assert.True(run.WorkspaceWritable);
        Assert.Equal(TimeSpan.FromSeconds(90), run.Timeout);
    }

    [Theory]
    [InlineData("python")]
    [InlineData("dev-foundry/sandbox-dotnet:local")]
    [InlineData("")]
    public void Only_allowlisted_image_aliases_are_accepted(string image) =>
        Rejected(Support.Request(image: image));

    [Theory]
    [InlineData("../etc")]
    [InlineData("a/b")]
    [InlineData("/abs")]
    [InlineData("a..b")]
    [InlineData("UPPER")]
    [InlineData("")]
    [InlineData(".hidden")]
    public void Workspace_must_be_a_plain_directory_name(string workspace) =>
        Rejected(Support.Request(workspace: workspace));

    [Fact]
    public void Missing_workspace_is_rejected() =>
        Assert.Contains("does not exist", Rejected(Support.Request(), exists: false).Message);

    [Fact]
    public void Command_must_not_be_empty() => Rejected(Support.Request(command: []));

    [Fact]
    public void Command_length_and_content_are_limited()
    {
        Rejected(Support.Request(command: [.. Enumerable.Repeat("x", SandboxPolicy.MaxArguments + 1)]));
        Rejected(Support.Request(command: [new string('x', SandboxPolicy.MaxArgumentLength + 1)]));
        Rejected(Support.Request(command: ["a\0b"]));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("LD_PRELOAD")]
    [InlineData("opencode_model")]
    [InlineData("OPENCODE_MODEL=x")]
    [InlineData("OPENCODE MODEL")]
    public void Only_allowed_environment_names_are_accepted(string name) =>
        Rejected(Support.Request(env: new() { [name] = "v" }));

    [Fact]
    public void Allowed_environment_is_passed_through()
    {
        var run = Support.Valid(Support.Request(env: new() { ["OPENCODE_MODEL"] = "openai/x", ["FACTORY_ROLE"] = "analyst" }));
        Assert.Contains("OPENCODE_MODEL=openai/x", run.Env);
        Assert.Contains("FACTORY_ROLE=analyst", run.Env);
    }

    [Fact]
    public void Timeout_is_required_and_capped()
    {
        Rejected(Support.Request(timeout: 0));
        Assert.Equal(TimeSpan.FromSeconds(1800), Support.Valid(Support.Request(timeout: 999_999)).Timeout);
    }
}
