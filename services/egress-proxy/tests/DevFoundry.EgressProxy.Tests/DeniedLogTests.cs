using System.Text.Json;

namespace DevFoundry.EgressProxy.Tests;

public sealed class DeniedLogTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
    private string Path_ => Path.Combine(folder, "denied.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static AccessEntry Entry(string decision, string host = "evil.example", string reason = "host evil.example is not on the developer list") =>
        new(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero), "developer", "10.250.3.5", host, 443, decision, reason, 0, 0, 12);

    [Fact]
    public void A_refusal_is_one_json_line_with_the_same_fields_as_the_console_log()
    {
        new DeniedLogFile(Path_).Write(Entry("deny"));

        var line = JsonDocument.Parse(Assert.Single(File.ReadAllLines(Path_))).RootElement;
        Assert.Equal("deny", line.GetProperty("decision").GetString());
        Assert.Equal("evil.example", line.GetProperty("host").GetString());
        Assert.Equal("developer", line.GetProperty("profile").GetString());
        Assert.Equal(443, line.GetProperty("port").GetInt32());
        Assert.Equal("host evil.example is not on the developer list", line.GetProperty("reason").GetString());
        Assert.StartsWith("2026-11-14T10:00:00", line.GetProperty("time").GetString());
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("audit")]
    public void Only_refusals_are_written(string decision)
    {
        new DeniedLogFile(Path_).Write(Entry(decision));

        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void Refusals_are_added_to_the_end_and_the_folder_is_made_when_it_is_missing()
    {
        var log = new DeniedLogFile(Path_);
        log.Write(Entry("deny", "a.example"));
        log.Write(Entry("deny", "b.example"));

        Assert.Equal(2, File.ReadAllLines(Path_).Length);
        Assert.Contains("a.example", File.ReadAllLines(Path_)[0]);
    }

    [Fact]
    public void The_file_stops_growing_at_its_limit_and_the_proxy_carries_on()
    {
        var log = new DeniedLogFile(Path_, maxBytes: 600);

        for (var i = 0; i < 20; i++)
        {
            log.Write(Entry("deny", $"host{i}.example"));
        }

        Assert.True(new FileInfo(Path_).Length <= 600, new FileInfo(Path_).Length.ToString());
        Assert.NotEmpty(File.ReadAllLines(Path_));
    }

    [Fact]
    public void A_file_that_cannot_be_written_never_makes_the_proxy_fail()
    {
        Directory.CreateDirectory(folder);
        var blocked = Path.Combine(folder, "a-folder");
        Directory.CreateDirectory(blocked); // a directory where the file should be

        var error = Record.Exception(() => new DeniedLogFile(blocked).Write(Entry("deny")));

        Assert.Null(error);
    }

    [Fact]
    public void The_tee_gives_every_entry_to_each_log()
    {
        var seen = new List<string>();
        var tee = new TeeAccessLog(new Capture(seen, "one"), new Capture(seen, "two"));

        tee.Write(Entry("deny"));

        Assert.Equal(["one", "two"], seen);
    }

    private sealed class Capture(List<string> seen, string name) : IAccessLog
    {
        public void Write(AccessEntry entry) => seen.Add(name);
    }
}
