using System.Diagnostics;
using Temporalio.Exceptions;

namespace AiSdlcFactory.Controller.Tests;

/// <summary>Revising a pull request: the checkout starts from the pull request's branch and the result is pushed onto that same branch.</summary>
public sealed class ExistingBranchTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "eb-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly string origin;
    private const string Name = "revision-acme-shop-7-abcd1234";
    private const string Branch = "factory/issue-3";

    public ExistingBranchTests()
    {
        root = Path.Combine(temp, "root");
        Directory.CreateDirectory(root);
        origin = Seed();
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

    /// <summary>A repository whose main branch has app.py and whose pull request branch adds feature.py on top.</summary>
    private string Seed()
    {
        var seed = Path.Combine(temp, "seed");
        var bare = Path.Combine(temp, "origin.git");
        Directory.CreateDirectory(seed);
        Git(seed, "init", "--quiet", "--initial-branch=main");
        File.WriteAllText(Path.Combine(seed, "app.py"), "print('hello')\n");
        Directory.CreateDirectory(Path.Combine(seed, ".factory"));
        File.WriteAllText(Path.Combine(seed, ".factory", "project.json"), "{}");
        Git(seed, "add", ".");
        Git(seed, "commit", "--quiet", "-m", "seed");
        Git(seed, "checkout", "--quiet", "-b", Branch);
        File.WriteAllText(Path.Combine(seed, "feature.py"), "def goodbye():\n    return 'bye'\n");
        Git(seed, "add", ".");
        Git(seed, "commit", "--quiet", "-m", "the pull request's commit");
        Directory.CreateDirectory(bare);
        Git(bare, "init", "--quiet", "--bare", "--initial-branch=main");
        Git(seed, "push", "--quiet", bare, "main", Branch);
        return bare;
    }

    private GitWorkspace Workspace() =>
        new(new WorkerSettings { Hosts = TestHosts.One(), WorkspaceRoot = root }, (_, _) => origin);

    private static Task<WorkspaceInfo> Prepare(GitWorkspace workspace, string branch = Branch, bool existing = true) =>
        workspace.PrepareAsync(new WorkspaceRequest("acme", "shop", Name, branch, existing), CancellationToken.None);

    private string RemoteHead() => Git(origin, "rev-parse", Branch).Trim();

    [Fact]
    public async Task The_checkout_starts_from_the_pull_requests_branch_not_from_the_default_branch()
    {
        var info = await Prepare(Workspace());

        Assert.Equal(Branch, info.Branch);
        Assert.Equal(RemoteHead(), info.HeadCommit);
        Assert.True(File.Exists($"{info.Path}/feature.py")); // the pull request's own file is there
        Assert.Equal("main", info.DefaultBranch);
    }

    [Fact]
    public async Task A_branch_that_does_not_exist_fails_for_good_with_its_name()
    {
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() => Prepare(Workspace(), "factory/issue-99"));

        Assert.True(error.NonRetryable);
        Assert.Contains("factory/issue-99 does not exist", error.Message);
    }

    [Fact]
    public async Task A_new_ticket_still_starts_from_the_default_branch()
    {
        var info = await Prepare(Workspace(), "factory/issue-50", existing: false);

        Assert.False(File.Exists($"{info.Path}/feature.py"));
        Assert.Equal(Git(origin, "rev-parse", "main").Trim(), info.HeadCommit);
    }

    [Fact]
    public async Task The_change_is_pushed_onto_the_same_branch_as_one_new_commit()
    {
        var workspace = Workspace();
        var info = await Prepare(workspace);
        var before = RemoteHead();
        File.WriteAllText($"{info.Path}/feature.py", "def goodbye():\n    return 'Goodbye!'\n");

        var result = await workspace.PublishAsync(new PublishRequest(Name, "Address review comments on #7", UpdateExistingBranch: true), CancellationToken.None);

        Assert.Equal(Branch, result!.Branch);
        Assert.Equal(1, result.FileCount);
        Assert.NotEqual(before, RemoteHead());
        Assert.Equal(before, Git(origin, "rev-parse", $"{Branch}~1").Trim()); // exactly one commit on top of the pull request
        Assert.Contains("Goodbye!", Git(origin, "show", $"{Branch}:feature.py"));
        Assert.Equal("Address review comments on #7", Git(origin, "log", "-1", "--format=%s", Branch).Trim());
        Assert.DoesNotContain("factory/issue-3-2", Git(origin, "branch", "--list").Split('\n').Select(l => l.Trim())); // no new branch name
    }

    [Fact]
    public async Task A_repeated_publish_after_the_push_does_not_lose_the_change_or_push_twice()
    {
        var workspace = Workspace();
        var info = await Prepare(workspace);
        File.AppendAllText($"{info.Path}/feature.py", "# reviewed\n");
        var request = new PublishRequest(Name, "Address review comments", UpdateExistingBranch: true);

        var first = await workspace.PublishAsync(request, CancellationToken.None);
        var head = RemoteHead();
        var second = await workspace.PublishAsync(request, CancellationToken.None);

        Assert.NotNull(second); // the inspection still sees the agent's change; it is not mistaken for "no changes"
        Assert.Equal(first!.CommitSha, second!.CommitSha);
        Assert.Equal(head, RemoteHead());
    }

    private void PersonPushes(string file, string text)
    {
        var person = Path.Combine(temp, "person" + Guid.NewGuid().ToString("N")[..6]);
        Git(temp, "clone", "--quiet", origin, person);
        Git(person, "checkout", "--quiet", Branch);
        File.WriteAllText(Path.Combine(person, file), text);
        Git(person, "add", ".");
        Git(person, "commit", "--quiet", "-m", "a person's commit");
        Git(person, "push", "--quiet", "origin", Branch);
    }

    [Fact]
    public async Task If_someone_pushed_to_the_branch_meanwhile_the_commit_goes_on_top_of_theirs()
    {
        var workspace = Workspace();
        var info = await Prepare(workspace);
        File.AppendAllText($"{info.Path}/feature.py", "# the factory's change\n");
        PersonPushes("notes.txt", "a person's work\n");
        var personsHead = RemoteHead();

        var published = await workspace.PublishAsync(new PublishRequest(Name, "Address review comments", UpdateExistingBranch: true), CancellationToken.None);

        Assert.Equal(1, published!.RebasedOver);
        Assert.Equal(published.CommitSha, RemoteHead());
        Assert.Equal(personsHead, Git(origin, "rev-parse", Branch + "~1").Trim()); // theirs is the parent, never rewritten
    }

    [Fact]
    public async Task If_the_persons_commit_conflicts_nothing_is_pushed_and_theirs_stays_the_tip()
    {
        var workspace = Workspace();
        var info = await Prepare(workspace);
        File.AppendAllText($"{info.Path}/feature.py", "# the factory's change\n");
        PersonPushes("feature.py", "completely different\n");
        var personsHead = RemoteHead();

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            workspace.PublishAsync(new PublishRequest(Name, "Address review comments", UpdateExistingBranch: true), CancellationToken.None));

        Assert.True(error.NonRetryable);
        Assert.Contains("conflict", error.Message);
        Assert.Equal(personsHead, RemoteHead());
    }

    [Fact]
    public async Task Only_verified_files_are_pushed_a_protected_file_still_refuses_the_publish()
    {
        var workspace = Workspace();
        var info = await Prepare(workspace);
        File.WriteAllText($"{info.Path}/.factory/project.json", "{\"image\":\"evil\"}");

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            workspace.PublishAsync(new PublishRequest(Name, "x", UpdateExistingBranch: true), CancellationToken.None));

        Assert.Contains("Refusing to publish", error.Message);
        Assert.Equal("{}", Git(origin, "show", $"{Branch}:.factory/project.json"));
    }

    [Fact]
    public async Task The_run_record_goes_onto_the_pull_request_branch_with_the_revision()
    {
        var workspace = Workspace();
        var info = await Prepare(workspace);
        File.AppendAllText($"{info.Path}/feature.py", "# reviewed\n");
        await workspace.RecordAsync(new RecordEntry(Name, 1, "agent", "pr-resolver", "pr-resolver", 1, "2026-11-05 10:00:00Z", 1.5, "accepted", 0, false, "the prompt", "the answer"));

        await workspace.PublishAsync(new PublishRequest(Name, "Address review comments", new RunRecordSpec(".factory/runs/pr-7-abcd1234", "acme/shop#7", "Pushed."), UpdateExistingBranch: true), CancellationToken.None);

        var files = Git(origin, "ls-tree", "-r", "--name-only", Branch).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(".factory/runs/pr-7-abcd1234/001-pr-resolver.md", files);
        Assert.Contains(".factory/runs/pr-7-abcd1234/README.md", files);
    }

    [Fact]
    public void The_record_of_a_revision_has_its_own_folder_named_after_the_pull_request()
    {
        Assert.Equal(".factory/runs/pr-7-0123abcd", RunRecord.DirectoryForRevision(7, "0123ABCD-ffff"));
        Assert.Equal(".factory/runs/qa-7-0123abcd", RunRecord.DirectoryForQa(7, "0123ABCD-ffff"));
        Assert.True(RunRecord.IsValidDirectory(RunRecord.DirectoryForQa(7, "0123ABCD-ffff")));
        Assert.True(RunRecord.IsValidDirectory(RunRecord.DirectoryForRevision(7, "0123ABCD-ffff")));
    }
}
