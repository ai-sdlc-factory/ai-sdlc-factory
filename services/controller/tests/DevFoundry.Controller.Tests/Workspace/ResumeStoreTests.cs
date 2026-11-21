namespace DevFoundry.Controller.Tests;

/// <summary>The saved stage results: one small file per ticket, kept when a checkout is cleaned up and removed with the janitor's delete.</summary>
public sealed class ResumeStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rs-" + Guid.NewGuid().ToString("N"));
    private const string Name = "feature-acme-shop-7-abcd1234";

    public ResumeStoreTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private GitWorkspace Workspace() => new(new WorkerSettings { Hosts = TestHosts.Forgejo(), WorkspaceRoot = root });

    private static ResumeState Full() => new(
        ResumeState.CurrentVersion, "fingerprint", "abc123",
        Analysis: new AnalystVerdict("pass", "Clear.", ["goodbye() returns the text.", "bye selects it."], [], new Complexity(2, false, true, false, false)),
        Coverage: new CoverageReport(CoverageVerdicts.PartlyCovered, "Checked.",
            [new CriterionCoverage(1, "goodbye() returns the text.", CoverageStatus.Covered, ["hello.py"], "there"), new CriterionCoverage(2, "bye selects it.", CoverageStatus.Missing, [], "")]),
        Design: new ArchitectDesign("pass", "Small.", "Add goodbye().", [new ComponentChange("hello.py", "add goodbye()")], ["none"], [], ["keep hello"]),
        BugAnalysis: new BugAnalysis("pass", "Off by one.", "ababab", "abab", ["repeat()"], "medium", []));

    [Fact]
    public async Task Every_stage_result_survives_being_saved_and_read_back()
    {
        var workspace = Workspace();

        await workspace.SaveResumeAsync(new SaveResumeRequest(Name, Full()));
        var loaded = workspace.LoadResume(Name)!;

        Assert.Equal("fingerprint", loaded.Fingerprint);
        Assert.Equal(["goodbye() returns the text.", "bye selects it."], loaded.Analysis!.AcceptanceCriteria);
        Assert.Equal(2, loaded.Analysis.Complexity.FilesEstimate);
        Assert.True(loaded.Analysis.Complexity.PublicApiChange);
        Assert.Equal(["bye selects it."], loaded.Coverage!.Remaining.Select(c => c.Criterion)); // the covered one is not built again
        Assert.Equal("hello.py", loaded.Coverage.Criteria[0].Evidence.Single());
        Assert.Equal("add goodbye()", loaded.Design!.Components.Single().Change);
        Assert.Equal("ababab", loaded.BugAnalysis!.Expected);
    }

    [Fact]
    public void Nothing_saved_reads_as_none()
    {
        Assert.Null(Workspace().LoadResume(Name));
    }

    [Fact]
    public async Task A_later_save_replaces_the_earlier_one_and_no_temporary_file_is_left()
    {
        var workspace = Workspace();
        await workspace.SaveResumeAsync(new SaveResumeRequest(Name, Full() with { Design = null }));
        await workspace.SaveResumeAsync(new SaveResumeRequest(Name, Full()));

        Assert.NotNull(workspace.LoadResume(Name)!.Design);
        Assert.Equal([$"{Name}.json"], Directory.GetFiles($"{root}/.resume").Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"Version\": ")]
    [InlineData("")]
    public async Task A_file_that_cannot_be_read_is_the_same_as_none(string content)
    {
        var workspace = Workspace();
        await workspace.SaveResumeAsync(new SaveResumeRequest(Name, Full()));
        File.WriteAllText($"{root}/.resume/{Name}.json", content);

        Assert.Null(workspace.LoadResume(Name));
    }

    [Fact]
    public async Task A_record_that_is_too_large_is_not_saved_instead_of_failing_the_run()
    {
        var workspace = Workspace();
        var huge = Full() with { Analysis = new AnalystVerdict("pass", new string('x', 300_000), ["c"], [], new Complexity(1, false, false, false, false)) };

        await workspace.SaveResumeAsync(new SaveResumeRequest(Name, huge));

        Assert.Null(workspace.LoadResume(Name));
    }

    [Fact]
    public async Task A_symbolic_link_in_place_of_the_file_is_not_followed()
    {
        var workspace = Workspace();
        Directory.CreateDirectory($"{root}/.resume");
        var secret = Path.Combine(root, "elsewhere.json");
        File.WriteAllText(secret, System.Text.Json.JsonSerializer.Serialize(Full()));
        File.CreateSymbolicLink($"{root}/.resume/{Name}.json", secret);

        Assert.Null(workspace.LoadResume(Name));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task A_name_that_is_not_a_workspace_name_is_refused(string name)
    {
        var workspace = Workspace();

        await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() => workspace.SaveResumeAsync(new SaveResumeRequest(name, Full())));
        Assert.Throws<Temporalio.Exceptions.ApplicationFailureException>(() => workspace.LoadResume(name));
    }

    [Fact]
    public async Task Cleaning_up_a_runs_checkout_keeps_the_saved_stages_but_the_janitors_delete_removes_them()
    {
        var workspace = Workspace();
        Directory.CreateDirectory($"{root}/{Name}");
        await workspace.SaveResumeAsync(new SaveResumeRequest(Name, Full()));

        await workspace.CleanupAsync(Name);
        Assert.NotNull(workspace.LoadResume(Name)); // a /retry still finds them

        await workspace.DeleteAsync(Name);
        Assert.Null(workspace.LoadResume(Name));
    }

    [Fact]
    public async Task Clearing_removes_only_that_tickets_record_and_clearing_nothing_is_fine()
    {
        var workspace = Workspace();
        await workspace.SaveResumeAsync(new SaveResumeRequest(Name, Full()));
        await workspace.SaveResumeAsync(new SaveResumeRequest("feature-acme-shop-8-abcd1234", Full()));

        workspace.ClearResume(Name);
        workspace.ClearResume(Name);

        Assert.Null(workspace.LoadResume(Name));
        Assert.NotNull(workspace.LoadResume("feature-acme-shop-8-abcd1234"));
    }

    [Fact]
    public async Task The_janitor_sees_a_ticket_with_only_saved_stages_and_counts_them_as_part_of_its_run()
    {
        var workspace = Workspace();
        Directory.CreateDirectory($"{root}/{Name}");
        Directory.SetLastWriteTimeUtc($"{root}/{Name}", DateTime.UtcNow.AddDays(-10));
        await workspace.SaveResumeAsync(new SaveResumeRequest(Name, Full()));
        await workspace.SaveResumeAsync(new SaveResumeRequest("feature-acme-shop-9-abcd1234", Full()));
        File.SetLastWriteTimeUtc($"{root}/.resume/feature-acme-shop-9-abcd1234.json", DateTime.UtcNow.AddDays(-5));

        var entries = workspace.List().ToDictionary(entry => entry.Name);

        Assert.True(entries[Name].LastTouched > DateTimeOffset.UtcNow.AddMinutes(-5)); // the fresh record keeps the old checkout from looking stale
        Assert.InRange(entries["feature-acme-shop-9-abcd1234"].LastTouched, DateTimeOffset.UtcNow.AddDays(-6), DateTimeOffset.UtcNow.AddDays(-4));
    }
}
