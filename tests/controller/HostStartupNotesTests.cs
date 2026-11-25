namespace AiSdlcFactory.Controller.Tests;

/// <summary>The warning at start for a GitHub host that lets anyone use commands: said for an open GitHub host, and only for that.</summary>
public sealed class HostStartupNotesTests
{
    private static HostSettings Host(string type, HostCommandRules? commands = null) =>
        new("host", type, "https://example.test", "token", "bot", null, commands);

    private static HostCommandRules Rule(CommandRule rule) => new(rule, new Dictionary<string, CommandRule>());

    [Fact]
    public void A_github_host_with_no_rule_is_warned_about_and_the_warning_says_what_to_set()
    {
        var warning = HostStartupNotes.OpenCommandsOnGitHub(Host("github"));

        Assert.NotNull(warning);
        Assert.Contains("\"who\":\"write\"", warning);
        Assert.Contains("docs/git-hosts.md", warning);
    }

    [Fact]
    public void A_github_host_that_holds_people_back_is_not_warned_about()
    {
        Assert.Null(HostStartupNotes.OpenCommandsOnGitHub(Host("github", Rule(new CommandRule(CommandLevel.Write)))));
        Assert.Null(HostStartupNotes.OpenCommandsOnGitHub(Host("github", Rule(new CommandRule(Deny: new HashSet<string> { "mallory" })))));
    }

    [Fact]
    public void A_forgejo_host_is_not_warned_about_even_with_no_rule()
    {
        Assert.Null(HostStartupNotes.OpenCommandsOnGitHub(Host("forgejo")));
    }
}
