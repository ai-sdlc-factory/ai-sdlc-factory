using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using Temporalio.Exceptions;

namespace DevFoundry.Controller.Tests;

public sealed class GitWorkspaceTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "gw-" + Guid.NewGuid().ToString("N"));

    public GitWorkspaceTests() => Directory.CreateDirectory(temp);

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private static void Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "t";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "t@example.com";
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    /// <summary>A local repository standing in for a git host; returns its path.</summary>
    private string Origin()
    {
        var origin = Path.Combine(temp, "origin");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "--quiet", "--initial-branch=main");
        File.WriteAllText(Path.Combine(origin, "README.md"), "hello");
        Git(origin, "add", ".");
        Git(origin, "commit", "--quiet", "-m", "first commit");
        return origin;
    }

    private GitWorkspace Workspace(string root, string origin) =>
        new(new WorkerSettings { Hosts = TestHosts.One(), WorkspaceRoot = root }, (_, _) => origin);

    private static WorkspaceRequest Request(string name = "feature-acme-shop-1") =>
        new("acme", "shop", name, "factory/issue-1");

    [Fact]
    public async Task Prepare_clones_the_repository_onto_a_new_working_branch()
    {
        var root = Path.Combine(temp, "root");
        Directory.CreateDirectory(root);

        var info = await Workspace(root, Origin()).PrepareAsync(Request(), CancellationToken.None);

        Assert.Equal($"{root}/feature-acme-shop-1", info.Path);
        Assert.Equal("factory/issue-1", info.Branch);
        Assert.Equal("main", info.DefaultBranch);
        Assert.Equal(40, info.HeadCommit.Length);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(info.Path, "README.md")));
        Assert.Contains("factory/issue-1", File.ReadAllText(Path.Combine(info.Path, ".git", "HEAD")));
    }

    [Fact]
    public async Task Prepare_twice_starts_from_a_clean_checkout()
    {
        var root = Path.Combine(temp, "root");
        Directory.CreateDirectory(root);
        var workspace = Workspace(root, Origin());
        var first = await workspace.PrepareAsync(Request(), CancellationToken.None);
        File.WriteAllText(Path.Combine(first.Path, "leftover.txt"), "from a failed attempt");

        var second = await workspace.PrepareAsync(Request(), CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(second.Path, "leftover.txt")));
    }

    [Fact]
    public async Task Token_is_not_written_into_the_repository()
    {
        var root = Path.Combine(temp, "root");
        Directory.CreateDirectory(root);
        var workspace = new GitWorkspace(new WorkerSettings { WorkspaceRoot = root, Hosts = TestHosts.One("secret-token-value") }, (_, _) => Origin());

        var info = await workspace.PrepareAsync(Request(), CancellationToken.None);

        Assert.DoesNotContain("secret-token-value", File.ReadAllText(Path.Combine(info.Path, ".git", "config")));
    }

    [Fact]
    public async Task Commits_are_made_as_the_configured_author()
    {
        var root = Path.Combine(temp, "root");
        Directory.CreateDirectory(root);
        var settings = new WorkerSettings { WorkspaceRoot = root, Hosts = TestHosts.One(), GitAuthorName = "Shop Bot", GitAuthorEmail = "12+shop-bot@users.noreply.example" };
        var info = await new GitWorkspace(settings, (_, _) => Origin()).PrepareAsync(Request(), CancellationToken.None);

        var config = File.ReadAllText(Path.Combine(root, ".base", info.Name, ".git", "config"));

        Assert.Contains("name = Shop Bot", config);
        Assert.Contains("email = 12+shop-bot@users.noreply.example", config);
    }

    [Fact]
    public void The_author_is_a_setting_that_must_be_one_clean_line()
    {
        WorkerSettings Load(string? name, string? email) => WorkerSettings.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(TestHosts.Setting)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FACTORY_GIT_AUTHOR_NAME"] = name, ["FACTORY_GIT_AUTHOR_EMAIL"] = email }).Build());

        var standard = Load(null, "  ");
        Assert.Equal("Dev Foundry", standard.GitAuthorName);
        Assert.Equal("factory-bot@dev-foundry.test", standard.GitAuthorEmail);
        var custom = Load(" Shop Bot ", "bot@example.org");
        Assert.Equal("Shop Bot", custom.GitAuthorName);
        Assert.Equal("bot@example.org", custom.GitAuthorEmail);
        Assert.Contains("FACTORY_GIT_AUTHOR_NAME", Assert.Throws<InvalidOperationException>(() => Load("Bot <x@y>", null)).Message);
        Assert.Throws<InvalidOperationException>(() => Load(null, "a@b\nc"));
    }

    [Fact]
    public async Task Cleanup_removes_only_the_named_workspace()
    {
        var root = Path.Combine(temp, "root");
        Directory.CreateDirectory(root);
        var workspace = Workspace(root, Origin());
        await workspace.PrepareAsync(Request("feature-a-1"), CancellationToken.None);
        await workspace.PrepareAsync(Request("feature-b-1"), CancellationToken.None);

        await workspace.CleanupAsync("feature-a-1");

        Assert.False(Directory.Exists($"{root}/feature-a-1"));
        Assert.True(Directory.Exists($"{root}/feature-b-1"));
    }

    [Fact]
    public async Task Cleanup_of_a_symlink_never_follows_it()
    {
        var root = Path.Combine(temp, "root");
        var outside = Path.Combine(temp, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "important");
        Directory.CreateSymbolicLink($"{root}/feature-link-1", outside);

        await Workspace(root, Origin()).CleanupAsync("feature-link-1");

        Assert.False(Directory.Exists($"{root}/feature-link-1"));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("UPPER")]
    [InlineData("")]
    public async Task Names_that_could_escape_the_root_are_rejected(string name)
    {
        var root = Path.Combine(temp, "root");
        Directory.CreateDirectory(root);
        var workspace = Workspace(root, Origin());

        await Assert.ThrowsAsync<ApplicationFailureException>(() => workspace.PrepareAsync(Request(name), CancellationToken.None));
        await Assert.ThrowsAsync<ApplicationFailureException>(() => workspace.CleanupAsync(name));
    }

    [Theory]
    [InlineData("..", "shop")]
    [InlineData("acme", "../shop")]
    [InlineData("acme", "sh op")]
    public async Task Owner_and_repository_names_are_validated(string owner, string repo)
    {
        var root = Path.Combine(temp, "root");
        Directory.CreateDirectory(root);
        var request = new WorkspaceRequest(owner, repo, "feature-x-1", "factory/issue-1");

        await Assert.ThrowsAsync<ApplicationFailureException>(
            () => Workspace(root, Origin()).PrepareAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_workspace_root_is_a_configuration_error()
    {
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => new GitWorkspace(new WorkerSettings { Hosts = TestHosts.One() }).PrepareAsync(Request(), CancellationToken.None));
        Assert.True(error.NonRetryable);
    }
}
