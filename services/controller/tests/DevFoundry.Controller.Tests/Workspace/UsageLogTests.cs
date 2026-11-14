using System.Text.Json;

namespace DevFoundry.Controller.Tests;

/// <summary>The usage log: one line per finished run on the host, readable by tools/usage-report.py and by a person.</summary>
public sealed class UsageLogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ul-" + Guid.NewGuid().ToString("N"));

    public UsageLogTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private GitWorkspace Workspace() => new(new WorkerSettings { WorkspaceRoot = root });

    private static UsageLogEntry Entry(string run = "run-1", long total = 1710) => new(
        "feature-acme-shop-1-abcd1234", run, "feature", "acme/shop", 1, "pr_opened",
        new TokenUsage(Input: 1000, Output: 200, Reasoning: 10, CacheRead: 500, CacheWrite: 3, Total: total, Cost: 0.25, Steps: 5));

    private string[] Lines() => File.ReadAllLines(GitWorkspace.UsageLogPath(root));

    [Fact]
    public async Task A_run_is_one_json_line_with_fixed_names_and_the_time_of_logging()
    {
        await Workspace().LogUsageAsync(Entry(), new DateTimeOffset(2026, 11, 14, 10, 30, 5, TimeSpan.FromHours(2)));

        var line = JsonDocument.Parse(Assert.Single(Lines())).RootElement;
        Assert.Equal("2026-11-14T08:30:05Z", line.GetProperty("time").GetString()); // always UTC
        Assert.Equal("feature-acme-shop-1-abcd1234", line.GetProperty("workflow_id").GetString());
        Assert.Equal("run-1", line.GetProperty("run_id").GetString());
        Assert.Equal("feature", line.GetProperty("kind").GetString());
        Assert.Equal("acme/shop", line.GetProperty("repository").GetString());
        Assert.Equal(1, line.GetProperty("number").GetInt32());
        Assert.Equal("pr_opened", line.GetProperty("status").GetString());
        Assert.Equal(
            (1000L, 200L, 10L, 500L, 3L, 1710L, 5, false),
            (line.GetProperty("input").GetInt64(), line.GetProperty("output").GetInt64(), line.GetProperty("reasoning").GetInt64(),
             line.GetProperty("cache_read").GetInt64(), line.GetProperty("cache_write").GetInt64(), line.GetProperty("total").GetInt64(),
             line.GetProperty("steps").GetInt32(), line.GetProperty("partial").GetBoolean()));
        Assert.Equal(0.25, line.GetProperty("cost").GetDouble());
    }

    [Fact]
    public async Task Each_run_adds_a_line_and_earlier_lines_are_never_changed()
    {
        var workspace = Workspace();
        await workspace.LogUsageAsync(Entry("run-1"), DateTimeOffset.UtcNow);
        var first = Lines()[0];
        await workspace.LogUsageAsync(Entry("run-2", 5), DateTimeOffset.UtcNow);

        Assert.Equal(2, Lines().Length);
        Assert.Equal(first, Lines()[0]);
        Assert.Contains("\"run_id\":\"run-2\"", Lines()[1]);
    }

    [Fact]
    public async Task Runs_that_finish_at_the_same_time_never_mix_their_lines()
    {
        var workspace = Workspace();

        await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(() => workspace.LogUsageAsync(Entry($"run-{i}", i), DateTimeOffset.UtcNow))));

        var lines = Lines();
        Assert.Equal(60, lines.Length);
        Assert.Equal(60, lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("run_id").GetString()).Distinct().Count()); // every line parses on its own
    }

    [Fact]
    public async Task The_log_is_under_the_workspace_root_in_a_folder_no_run_name_can_have()
    {
        await Workspace().LogUsageAsync(Entry(), DateTimeOffset.UtcNow);

        Assert.True(File.Exists($"{root}/.usage/usage.jsonl"));
        Assert.DoesNotContain(Workspace().List(), entry => entry.Name.Contains("usage")); // the janitor does not see it as a run's folder
    }

    [Fact]
    public async Task Text_with_quotes_and_line_breaks_stays_one_line()
    {
        await Workspace().LogUsageAsync(Entry() with { Repository = "acme/\"shop\"\nnext" }, DateTimeOffset.UtcNow);

        var line = Assert.Single(Lines());
        Assert.Equal("acme/\"shop\"\nnext", JsonDocument.Parse(line).RootElement.GetProperty("repository").GetString());
    }
}
