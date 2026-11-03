using System.Diagnostics;
using Temporalio.Exceptions;

namespace DevFoundry.Controller.Tests;

public sealed class WorkspaceChangesTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "wc-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly string origin;

    public WorkspaceChangesTests()
    {
        root = Path.Combine(temp, "root");
        Directory.CreateDirectory(root);
        origin = SeedBareOrigin();
    }

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "t";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "t@example.com";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    /// <summary>A bare repository standing in for Forgejo, seeded with a few files.</summary>
    private string SeedBareOrigin()
    {
        var seed = Path.Combine(temp, "seed");
        var bare = Path.Combine(temp, "origin.git");
        Directory.CreateDirectory(seed);
        Git(seed, "init", "--quiet", "--initial-branch=main");
        File.WriteAllText(Path.Combine(seed, "app.py"), "print('hello')\n");
        File.WriteAllText(Path.Combine(seed, "old.py"), "# to be deleted\n");
        File.WriteAllText(Path.Combine(seed, ".gitignore"), "*.log\nbuild/\n");
        Directory.CreateDirectory(Path.Combine(seed, ".factory"));
        File.WriteAllText(Path.Combine(seed, ".factory", "project.json"), """{"image":"python","gates":[{"name":"test","command":["pytest"]}]}""");
        Git(seed, "add", ".");
        Git(seed, "commit", "--quiet", "-m", "seed");
        Directory.CreateDirectory(bare);
        Git(bare, "init", "--quiet", "--bare", "--initial-branch=main");
        Git(seed, "push", "--quiet", bare, "main");
        return bare;
    }

    private GitWorkspace Workspace(string token = "") =>
        new(new WorkerSettings { WorkspaceRoot = root, ForgejoToken = token }, (_, _) => origin);

    private async Task<WorkspaceInfo> Prepared(GitWorkspace workspace, string name = "feature-acme-shop-1") =>
        await workspace.PrepareAsync(new WorkspaceRequest("acme", "shop", name, "factory/issue-1"), CancellationToken.None);

    // ---- preparation -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_agent_gets_a_copy_and_the_pristine_clone_stays_separate()
    {
        var info = await Prepared(Workspace());

        Assert.True(File.Exists($"{info.Path}/app.py"));
        Assert.True(Directory.Exists($"{info.Path}/.git"));
        Assert.True(File.Exists($"{root}/.base/feature-acme-shop-1/app.py"));
        Assert.NotEqual(info.Path, $"{root}/.base/feature-acme-shop-1");
    }

    [Fact]
    public async Task Cleanup_removes_both_copies()
    {
        var workspace = Workspace();
        await Prepared(workspace);

        await workspace.CleanupAsync("feature-acme-shop-1");

        Assert.False(Directory.Exists($"{root}/feature-acme-shop-1"));
        Assert.False(Directory.Exists($"{root}/.base/feature-acme-shop-1"));
    }

    [Fact]
    public async Task Project_config_comes_from_the_pristine_clone_even_if_the_agent_rewrites_it()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        File.WriteAllText($"{info.Path}/.factory/project.json", """{"image":"evil","gates":[]}""");

        Assert.Contains("\"image\":\"python\"", workspace.ReadProjectConfig(info.Name));
    }

    [Fact]
    public async Task Missing_or_linked_project_config_reads_as_null()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        File.Delete($"{root}/.base/{info.Name}/.factory/project.json");
        Assert.Null(workspace.ReadProjectConfig(info.Name));

        File.CreateSymbolicLink($"{root}/.base/{info.Name}/.factory/project.json", "/etc/hostname");
        Assert.Null(workspace.ReadProjectConfig(info.Name));
    }

    // ---- detecting changes -------------------------------------------------------------------------------

    [Fact]
    public async Task Added_modified_and_deleted_files_are_reported()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        File.AppendAllText($"{info.Path}/app.py", "print('more')\n");
        File.WriteAllText($"{info.Path}/new.py", "x = 1\n");
        File.Delete($"{info.Path}/old.py");
        Directory.CreateDirectory($"{info.Path}/pkg");
        File.WriteAllText($"{info.Path}/pkg/mod.py", "y = 2\n");

        var report = await workspace.DetectChangesAsync(info.Name, CancellationToken.None);

        Assert.Empty(report.Violations);
        Assert.Equal(
            [("app.py", "modified"), ("new.py", "added"), ("old.py", "deleted"), ("pkg/mod.py", "added")],
            report.Files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => (f.Path, f.Kind)));
    }

    [Fact]
    public async Task Ignored_files_are_not_changes()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        File.WriteAllText($"{info.Path}/debug.log", "noise");
        Directory.CreateDirectory($"{info.Path}/build");
        File.WriteAllText($"{info.Path}/build/out.bin", "noise");

        var report = await workspace.DetectChangesAsync(info.Name, CancellationToken.None);

        Assert.Empty(report.Files);
        Assert.Empty(report.Violations);
    }

    [Theory]
    [InlineData(".factory/project.json")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("bitbucket-pipelines.yml")]
    [InlineData(".forgejo/workflows/x.yml")]
    public async Task Protected_files_are_violations(string path)
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName($"{info.Path}/{path}")!);
        File.WriteAllText($"{info.Path}/{path}", "changed");

        var report = await workspace.DetectChangesAsync(info.Name, CancellationToken.None);

        Assert.Contains(report.Violations, v => v.Contains(path) && v.Contains("protected"));
    }

    [Fact]
    public async Task Symbolic_links_are_violations()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        File.CreateSymbolicLink($"{info.Path}/link.txt", "/etc/passwd");

        var report = await workspace.DetectChangesAsync(info.Name, CancellationToken.None);

        Assert.Contains(report.Violations, v => v.Contains("link.txt") && v.Contains("symbolic link"));
    }

    [Fact]
    public async Task A_directory_replaced_by_a_link_is_caught()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        var outside = Path.Combine(temp, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText($"{outside}/secret.txt", "x");
        Directory.CreateSymbolicLink($"{info.Path}/docs", outside);

        var report = await workspace.DetectChangesAsync(info.Name, CancellationToken.None);

        Assert.Contains(report.Violations, v => v.Contains("docs") && v.Contains("symbolic link"));
    }

    [Fact]
    public async Task Oversized_files_and_floods_of_files_are_violations()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        File.WriteAllBytes($"{info.Path}/big.bin", new byte[3 * 1024 * 1024]);
        Directory.CreateDirectory($"{info.Path}/generated");
        for (var i = 0; i < 301; i++)
        {
            File.WriteAllText($"{info.Path}/generated/f{i}.txt", "x");
        }

        var report = await workspace.DetectChangesAsync(info.Name, CancellationToken.None);

        Assert.Contains(report.Violations, v => v.Contains("big.bin") && v.Contains("limit per file"));
        Assert.Contains(report.Violations, v => v.Contains("files changed") && v.Contains(".gitignore"));
    }

    [Fact]
    public async Task Secrets_are_reported_by_place_never_by_value()
    {
        var workspace = Workspace(token: "factory-token-0123456789");
        var info = await Prepared(workspace);
        File.WriteAllText($"{info.Path}/config.py", "DEBUG = True\napi_key = \"abcd1234efgh5678ijkl\"\n");
        File.WriteAllText($"{info.Path}/notes.txt", "my token is factory-token-0123456789 ok\n");
        File.WriteAllText($"{info.Path}/key.pem", "-----BEGIN RSA PRIVATE KEY-----\nabc\n");

        var report = await workspace.DetectChangesAsync(info.Name, CancellationToken.None);
        var text = string.Join('\n', report.Violations);

        Assert.Contains("config.py:2", text);
        Assert.Contains("notes.txt:1", text);
        Assert.Contains("key.pem:1", text);
        Assert.DoesNotContain("abcd1234efgh5678ijkl", text);
        Assert.DoesNotContain("factory-token-0123456789", text);
    }

    [Fact]
    public async Task A_hostile_git_directory_in_the_workspace_never_runs_anything()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        var marker = Path.Combine(temp, "pwned");
        // A hook and config entries a malicious agent might plant in its own copy of .git.
        File.WriteAllText($"{info.Path}/.git/hooks/pre-commit", $"#!/bin/sh\ntouch {marker}\n");
        File.SetUnixFileMode($"{info.Path}/.git/hooks/pre-commit", (UnixFileMode)0b111_101_101);
        File.AppendAllText($"{info.Path}/.git/config", $"[core]\n\tfsmonitor = touch {marker}\n\thooksPath = {info.Path}/.git/hooks\n[alias]\n\tcommit = !touch {marker}\n");
        File.WriteAllText($"{info.Path}/new.py", "x = 1\n");

        await workspace.DetectChangesAsync(info.Name, CancellationToken.None);
        var result = await workspace.PublishAsync(new PublishRequest(info.Name, "add new"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(File.Exists(marker));
    }

    // ---- the diff for reviewers --------------------------------------------------------------------------

    [Fact]
    public async Task The_diff_shows_modified_deleted_and_new_files()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        File.AppendAllText($"{info.Path}/app.py", "print('more')\n");
        File.Delete($"{info.Path}/old.py");
        File.WriteAllText($"{info.Path}/new.py", "x = 1\ny = 2\n");

        var diff = await workspace.DiffAsync(info.Name, CancellationToken.None);

        Assert.Contains("diff --git a/app.py b/app.py", diff);
        Assert.Contains("+print('more')", diff);
        Assert.Contains("-# to be deleted", diff);
        Assert.Contains("diff --git a/new.py b/new.py", diff);
        Assert.Contains("+x = 1", diff);
        Assert.Contains("+y = 2", diff);
    }

    [Fact]
    public async Task Binary_and_large_new_files_are_named_but_not_shown()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        File.WriteAllBytes($"{info.Path}/logo.png", [0x89, 0x50, 0x00, 0x01, 0x02]);
        File.WriteAllText($"{info.Path}/big.txt", new string('x', 70 * 1024));

        var diff = await workspace.DiffAsync(info.Name, CancellationToken.None);

        Assert.Contains("logo.png", diff);
        Assert.Contains("binary, not shown", diff);
        Assert.Contains("big.txt", diff);
        Assert.Contains("large: 1 lines, not shown", diff);
        Assert.DoesNotContain(new string('x', 100), diff);
    }

    [Fact]
    public async Task Protected_files_never_appear_in_the_diff()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        File.WriteAllText($"{info.Path}/.factory/project.json", "{\"image\":\"evil\"}");
        File.WriteAllText($"{info.Path}/new.py", "x = 1\n");

        var diff = await workspace.DiffAsync(info.Name, CancellationToken.None);

        Assert.DoesNotContain("evil", diff);
        Assert.Contains("new.py", diff);
    }

    [Fact]
    public async Task Producing_the_diff_never_runs_anything_planted_in_the_workspace()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        var marker = Path.Combine(temp, "pwned-by-diff");
        File.AppendAllText($"{info.Path}/.git/config", $"[core]\n\tfsmonitor = touch {marker}\n[diff]\n\texternal = touch {marker}\n[diff \"x\"]\n\ttextconv = touch {marker}\n");
        File.WriteAllText($"{info.Path}/.gitattributes", "*.py diff=x\n");
        File.AppendAllText($"{info.Path}/app.py", "print('more')\n");

        var diff = await workspace.DiffAsync(info.Name, CancellationToken.None);

        Assert.Contains("+print('more')", diff);
        Assert.False(File.Exists(marker));
    }

    // ---- publishing --------------------------------------------------------------------------------------

    [Fact]
    public async Task Publish_commits_the_changes_on_the_branch_and_pushes_it()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        File.AppendAllText($"{info.Path}/app.py", "print('more')\n");
        File.WriteAllText($"{info.Path}/tool.sh", "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode($"{info.Path}/tool.sh", (UnixFileMode)0b111_101_101);
        File.Delete($"{info.Path}/old.py");

        var result = await workspace.PublishAsync(new PublishRequest(info.Name, "Add things (#1)\n\nBody."), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("factory/issue-1", result.Branch);
        Assert.Equal(3, result.FileCount);
        var tree = Git(origin, "ls-tree", "-r", "--name-only", "factory/issue-1").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("tool.sh", tree);
        Assert.DoesNotContain("old.py", tree);
        Assert.Equal(result.CommitSha, Git(origin, "rev-parse", "factory/issue-1").Trim());
        Assert.Equal("Add things (#1)", Git(origin, "log", "-1", "--format=%s", "factory/issue-1").Trim());
        Assert.Contains("100755", Git(origin, "ls-tree", "factory/issue-1", "tool.sh"));
        Assert.Contains("print('more')", Git(origin, "show", "factory/issue-1:app.py"));
    }

    [Fact]
    public async Task Publish_with_no_changes_returns_null_and_pushes_nothing()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);

        Assert.Null(await workspace.PublishAsync(new PublishRequest(info.Name, "nothing"), CancellationToken.None));
        Assert.Empty(Git(origin, "branch", "--list", "factory/issue-1").Trim());
    }

    [Fact]
    public async Task Publish_refuses_changes_with_violations_even_without_an_earlier_report()
    {
        var workspace = Workspace();
        var info = await Prepared(workspace);
        File.WriteAllText($"{info.Path}/.factory/project.json", "{}");

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => workspace.PublishAsync(new PublishRequest(info.Name, "sneaky"), CancellationToken.None));

        Assert.True(error.NonRetryable);
        Assert.Contains("protected", error.Message);
        Assert.Empty(Git(origin, "branch", "--list", "factory/issue-1").Trim());
    }
}

public sealed class ChangeChecksTests
{
    [Theory]
    [InlineData(".factory/project.json", true)]
    [InlineData(".FACTORY/x", true)]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData("bitbucket-pipelines.yml", true)]
    [InlineData("Jenkinsfile", true)]
    [InlineData("src/.factory/x", false)]
    [InlineData("factory/project.json", false)]
    [InlineData("docs/bitbucket-pipelines.yml.md", false)]
    public void Protected_paths_are_recognised(string path, bool expected) =>
        Assert.Equal(expected, ProtectedPaths.IsProtected(path));

    private static string[] Scan(string text, params string[] known) =>
        SecretScan.Scan("f.txt", System.Text.Encoding.UTF8.GetBytes(text), known).ToArray();

    [Theory]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    [InlineData("key = AKIAIOSFODNN7EXAMPLE")]
    [InlineData("t = ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("SLACK=xoxb-1234567890-abcdefghij")]
    [InlineData("password = 'correct-horse-battery'")]
    [InlineData("API_KEY: \"abcdef0123456789\"")]
    public void Common_credentials_are_found(string line) => Assert.Single(Scan(line));

    [Theory]
    [InlineData("password = os.environ['PASSWORD']")]
    [InlineData("token = \"short\"")]
    [InlineData("# set the api_key in your environment")]
    [InlineData("def secret_santa(): pass")]
    public void Ordinary_code_is_not_flagged(string line) => Assert.Empty(Scan(line));

    [Fact]
    public void Binary_and_oversized_content_is_handled()
    {
        Assert.Empty(SecretScan.Scan("img.png", [0x89, 0x50, 0x00, 0x01, .. "password = 'hunter2hunter2'"u8.ToArray()], []));
        Assert.Contains("not scanned", Assert.Single(SecretScan.Scan("big.txt", new byte[1024 * 1024 + 1], [])));
    }

    [Fact]
    public void The_factorys_own_token_is_found_by_exact_match_and_short_values_are_ignored()
    {
        Assert.Single(Scan("x = 'a1b2c3d4e5f6'", "a1b2c3d4e5f6"));
        Assert.Empty(Scan("x = abc", "abc"));
    }
}
