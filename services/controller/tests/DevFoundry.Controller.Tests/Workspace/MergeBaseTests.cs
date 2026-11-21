using System.Diagnostics;
using Temporalio.Exceptions;

namespace DevFoundry.Controller.Tests;

/// <summary>/update against real git: the base branch is merged in the trusted clone, the agent works on the result, and one merge commit with both parents is pushed.</summary>
public sealed class MergeBaseTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "mb-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly string origin;
    private const string Name = "update-acme-shop-7-abcd1234";
    private const string Branch = "factory/issue-3";

    public MergeBaseTests()
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

    /// <summary>
    /// main has app.py (a greeting), util.py and the project's settings; the pull request's branch adds feature.py and changes the greeting.
    /// Nothing has moved on main yet.
    /// </summary>
    private string Seed()
    {
        var seed = Path.Combine(temp, "seed");
        var bare = Path.Combine(temp, "origin.git");
        Directory.CreateDirectory(seed);
        Git(seed, "init", "--quiet", "--initial-branch=main");
        File.WriteAllText(Path.Combine(seed, "app.py"), "def greet():\n    return 'hello'\n\n\ndef farewell():\n    return 'bye'\n");
        File.WriteAllText(Path.Combine(seed, "util.py"), "VERSION = 1\n");
        Directory.CreateDirectory(Path.Combine(seed, ".factory"));
        File.WriteAllText(Path.Combine(seed, ".factory", "project.json"), "{}");
        Git(seed, "add", ".");
        Git(seed, "commit", "--quiet", "-m", "seed");
        Git(seed, "checkout", "--quiet", "-b", Branch);
        File.WriteAllText(Path.Combine(seed, "feature.py"), "def goodbye():\n    return 'bye'\n");
        File.WriteAllText(Path.Combine(seed, "app.py"), "def greet():\n    return 'hello from the pull request'\n\n\ndef farewell():\n    return 'bye'\n");
        Git(seed, "add", ".");
        Git(seed, "commit", "--quiet", "-m", "the pull request's commit");
        Directory.CreateDirectory(bare);
        Git(bare, "init", "--quiet", "--bare", "--initial-branch=main");
        Git(seed, "push", "--quiet", bare, "main", Branch);
        return bare;
    }

    /// <summary>Somebody else's work lands on a branch of the remote (a clone of its own, pushed back).</summary>
    private void Push(string branch, Action<string> change, string message = "work on the base")
    {
        var person = Path.Combine(temp, "person" + Guid.NewGuid().ToString("N")[..6]);
        Git(temp, "clone", "--quiet", origin, person);
        Git(person, "checkout", "--quiet", branch);
        change(person);
        Git(person, "add", ".");
        Git(person, "commit", "--quiet", "-m", message);
        Git(person, "push", "--quiet", "origin", branch);
    }

    private void MainMovesOnWithoutConflict() =>
        Push("main", dir => File.WriteAllText(Path.Combine(dir, "util.py"), "VERSION = 2\n"));

    private void MainChangesTheSameLine() =>
        Push("main", dir => File.WriteAllText(Path.Combine(dir, "app.py"), "def greet():\n    return 'hello from main'\n\n\ndef farewell():\n    return 'bye'\n"));

    private GitWorkspace Workspace() => new(new WorkerSettings { Hosts = TestHosts.One(), WorkspaceRoot = root }, (_, _) => origin);

    private static Task<WorkspaceInfo> Prepare(GitWorkspace workspace) =>
        workspace.PrepareAsync(new WorkspaceRequest("acme", "shop", Name, Branch, ExistingBranch: true), CancellationToken.None);

    private static Task<MergeResult> Merge(GitWorkspace workspace) =>
        workspace.MergeBaseAsync(new MergeBaseRequest(Name, "main"), CancellationToken.None);

    private string RemoteHead() => Git(origin, "rev-parse", Branch).Trim();

    [Fact]
    public async Task A_branch_that_has_everything_from_the_base_is_left_alone()
    {
        var workspace = Workspace();
        await Prepare(workspace);

        var result = await Merge(workspace);

        Assert.Equal(MergeResult.UpToDate, result.Outcome);
        Assert.Equal(Git(origin, "rev-parse", "main").Trim(), result.BaseCommit);
    }

    [Fact]
    public async Task A_clean_merge_hands_the_merged_tree_to_the_agent_and_shows_no_change_of_its_own()
    {
        MainMovesOnWithoutConflict();
        var workspace = Workspace();
        var info = await Prepare(workspace);

        var result = await Merge(workspace);

        Assert.Equal(MergeResult.Merged, result.Outcome);
        Assert.Empty(result.Conflicts);
        Assert.Equal(1, result.FilesBrought);
        Assert.Equal("VERSION = 2\n", File.ReadAllText($"{info.Path}/util.py")); // the agent's copy has what came in
        Assert.True(File.Exists($"{info.Path}/feature.py")); // and what the pull request has
        var changes = await workspace.DetectChangesAsync(Name, CancellationToken.None);
        Assert.Empty(changes.Files); // what the merge brought is not mistaken for the agent's own work
        Assert.Empty(changes.Violations);
        Assert.Empty(await workspace.FindConflictMarkersAsync(Name));
    }

    [Fact]
    public async Task A_clean_merge_is_pushed_as_a_merge_commit_with_both_parents_and_the_branch_only_moves_forward()
    {
        MainMovesOnWithoutConflict();
        var before = RemoteHead();
        var main = Git(origin, "rev-parse", "main").Trim();
        var workspace = Workspace();
        await Prepare(workspace);
        await Merge(workspace);

        var published = await workspace.PublishMergeAsync(new PublishMergeRequest(Name, "Merge branch 'main' into factory/issue-3"), CancellationToken.None);

        Assert.Equal(Branch, published.Branch);
        Assert.Equal(published.CommitSha, RemoteHead());
        Assert.Equal($"{before} {main}", Git(origin, "log", "-1", "--format=%P", Branch).Trim());
        Assert.Equal("Merge branch 'main' into factory/issue-3", Git(origin, "log", "-1", "--format=%s", Branch).Trim());
        Assert.Equal("VERSION = 2\n", Git(origin, "show", $"{Branch}:util.py"));
        Assert.Contains("goodbye", Git(origin, "show", $"{Branch}:feature.py"));
        Git(origin, "merge-base", "--is-ancestor", before, Branch); // nothing was rewritten
        Assert.Equal(before, Git(origin, "rev-parse", $"{Branch}^1").Trim());
    }

    [Fact]
    public async Task A_conflict_leaves_the_markers_in_the_agents_copy_and_names_the_file()
    {
        MainChangesTheSameLine();
        var workspace = Workspace();
        var info = await Prepare(workspace);

        var result = await Merge(workspace);

        Assert.Equal(MergeResult.Conflicted, result.Outcome);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("app.py", conflict.Path);
        Assert.Equal("both changed it", conflict.Kind);
        var text = File.ReadAllText($"{info.Path}/app.py");
        Assert.Contains("<<<<<<<", text);
        Assert.Contains("hello from main", text);
        Assert.Contains("hello from the pull request", text);
        Assert.Equal(["app.py"], await workspace.FindConflictMarkersAsync(Name));
        Assert.Empty((await workspace.DetectChangesAsync(Name, CancellationToken.None)).Files);
        Assert.Equal(Branch, Git(info.Path, "rev-parse", "--abbrev-ref", "HEAD").Trim()); // the agent's copy is on the branch, merge committed locally
        Assert.Equal(2, Git(info.Path, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Length - 1);
    }

    [Fact]
    public async Task Resolved_files_are_the_only_changes_and_publish_refuses_while_a_marker_is_left()
    {
        MainChangesTheSameLine();
        var workspace = Workspace();
        var info = await Prepare(workspace);
        await Merge(workspace);
        var before = RemoteHead();

        // Half done: the markers are still there. The change is visible, but nothing may be pushed.
        File.AppendAllText($"{info.Path}/app.py", "# half done\n");
        var refused = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            workspace.PublishMergeAsync(new PublishMergeRequest(Name, "Merge"), CancellationToken.None));
        Assert.Contains("conflict markers are still in app.py", refused.Message);
        Assert.Equal(before, RemoteHead());

        File.WriteAllText($"{info.Path}/app.py", "def greet():\n    return 'hello from main and the pull request'\n\n\ndef farewell():\n    return 'bye'\n");
        var changes = await workspace.DetectChangesAsync(Name, CancellationToken.None);
        Assert.Equal(["app.py"], changes.Files.Select(f => f.Path));
        Assert.Empty(await workspace.FindConflictMarkersAsync(Name));

        var published = await workspace.PublishMergeAsync(new PublishMergeRequest(Name, "Merge branch 'main' into factory/issue-3"), CancellationToken.None);

        Assert.Equal(1, published.FileCount);
        Assert.Equal(2, Git(origin, "rev-list", "--parents", "-n", "1", Branch).Trim().Split(' ').Length - 1); // a merge commit
        Assert.Equal(before, Git(origin, "rev-parse", $"{Branch}^1").Trim());
        Assert.Equal(Git(origin, "rev-parse", "main").Trim(), Git(origin, "rev-parse", $"{Branch}^2").Trim());
        var app = Git(origin, "show", $"{Branch}:app.py");
        Assert.Contains("hello from main and the pull request", app);
        Assert.DoesNotContain("<<<<<<<", app);
        Assert.Contains("goodbye", Git(origin, "show", $"{Branch}:feature.py")); // files git combined by itself are in the commit too
    }

    [Fact]
    public async Task A_file_one_side_deleted_and_the_other_changed_is_a_conflict_without_markers_that_the_agent_decides()
    {
        Push("main", dir => File.Delete(Path.Combine(dir, "util.py")), "delete util");
        // The pull request changes the file the base deleted.
        var workspace = Workspace();
        var info = await Prepare(workspace);
        File.WriteAllText($"{info.Path}/util.py", "VERSION = 3\n"); // the agent's copy is only for the check; the clone is what matters
        Push(Branch, dir => File.WriteAllText(Path.Combine(dir, "util.py"), "VERSION = 3\n"), "the pull request changes util");
        await Prepare(workspace);

        var result = await Merge(workspace);

        Assert.Equal(MergeResult.Conflicted, result.Outcome);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("util.py", conflict.Path);
        Assert.Equal("the base deleted it, the pull request changed it", conflict.Kind);
        Assert.Empty(await workspace.FindConflictMarkersAsync(Name)); // nothing to scan: a decision, not markers
    }

    [Fact]
    public async Task Merging_again_after_an_earlier_attempt_starts_from_the_branch_not_from_its_own_merge()
    {
        MainMovesOnWithoutConflict();
        var workspace = Workspace();
        await Prepare(workspace);

        var first = await Merge(workspace);
        var second = await Merge(workspace);

        Assert.Equal(MergeResult.Merged, first.Outcome);
        Assert.Equal(MergeResult.Merged, second.Outcome); // not "up to date" because the first attempt had already merged
        Assert.Equal(first.FilesBrought, second.FilesBrought);
    }

    [Fact]
    public async Task If_a_person_pushed_to_the_branch_meanwhile_nothing_is_pushed_and_theirs_stays_the_tip()
    {
        MainMovesOnWithoutConflict();
        var workspace = Workspace();
        await Prepare(workspace);
        await Merge(workspace);
        Push(Branch, dir => File.WriteAllText(Path.Combine(dir, "notes.txt"), "a person's work\n"), "a person's commit");
        var personsHead = RemoteHead();

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            workspace.PublishMergeAsync(new PublishMergeRequest(Name, "Merge"), CancellationToken.None));

        Assert.True(error.NonRetryable);
        Assert.Contains("changed while the update was being made", error.Message);
        Assert.Equal(personsHead, RemoteHead());
    }

    [Fact]
    public async Task A_repeated_publish_after_the_push_got_through_reports_the_same_merge_and_pushes_nothing_more()
    {
        MainMovesOnWithoutConflict();
        var workspace = Workspace();
        await Prepare(workspace);
        await Merge(workspace);
        var request = new PublishMergeRequest(Name, "Merge branch 'main' into factory/issue-3");

        var first = await workspace.PublishMergeAsync(request, CancellationToken.None);
        var head = RemoteHead();
        await Task.Delay(1100); // a new commit made a second later would have another id
        var second = await workspace.PublishMergeAsync(request, CancellationToken.None);

        Assert.Equal(first.CommitSha, second.CommitSha);
        Assert.Equal(head, RemoteHead());
    }

    [Fact]
    public async Task A_protected_file_changed_by_the_agent_still_refuses_the_publish_but_one_the_merge_brought_does_not()
    {
        Push("main", dir => File.WriteAllText(Path.Combine(dir, ".factory", "project.json"), "{\"image\":\"python\"}"), "settings change on main");
        var workspace = Workspace();
        var info = await Prepare(workspace);
        await Merge(workspace);

        // What the base changed in a protected file is not the agent's change.
        Assert.Empty((await workspace.DetectChangesAsync(Name, CancellationToken.None)).Violations);

        File.WriteAllText($"{info.Path}/.factory/project.json", "{\"image\":\"evil\"}");
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            workspace.PublishMergeAsync(new PublishMergeRequest(Name, "Merge"), CancellationToken.None));
        Assert.Contains("Refusing to publish", error.Message);
        Assert.Equal("{}", Git(origin, "show", $"{Branch}:.factory/project.json"));
    }

    [Fact]
    public async Task Histories_with_nothing_in_common_fail_for_good_with_a_reason()
    {
        var other = Path.Combine(temp, "other");
        Directory.CreateDirectory(other);
        Git(other, "init", "--quiet", "--initial-branch=elsewhere");
        File.WriteAllText(Path.Combine(other, "x.txt"), "x\n");
        Git(other, "add", ".");
        Git(other, "commit", "--quiet", "-m", "unrelated");
        Git(other, "push", "--quiet", origin, "elsewhere");
        var workspace = Workspace();
        await Prepare(workspace);

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            workspace.MergeBaseAsync(new MergeBaseRequest(Name, "elsewhere"), CancellationToken.None));

        Assert.True(error.NonRetryable);
        Assert.Contains("could not merge elsewhere into factory/issue-3", error.Message);
    }

    [Theory]
    [InlineData("--upload-pack=x")]
    [InlineData("../main")]
    [InlineData("")]
    public async Task A_base_branch_name_that_is_not_a_plain_branch_is_refused(string name)
    {
        var workspace = Workspace();
        await Prepare(workspace);

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            workspace.MergeBaseAsync(new MergeBaseRequest(Name, name), CancellationToken.None));

        Assert.True(error.NonRetryable);
    }
}
