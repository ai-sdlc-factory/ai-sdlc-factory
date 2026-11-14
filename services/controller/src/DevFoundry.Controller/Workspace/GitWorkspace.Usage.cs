using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevFoundry.Controller;

/// <summary>What a finished run used, as the workflow knows it. The activity adds the time.</summary>
public sealed record UsageLogEntry(string WorkflowId, string RunId, string Kind, string Repository, int Number, string Status, TokenUsage Usage);

/// <summary>One line of the usage log. Names are fixed (snake_case) because other tools read the file: <c>tools/usage-report.py</c> and whatever a person writes.</summary>
public sealed record UsageLine(
    [property: JsonPropertyName("time")] string Time,
    [property: JsonPropertyName("workflow_id")] string WorkflowId,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("input")] long Input,
    [property: JsonPropertyName("output")] long Output,
    [property: JsonPropertyName("reasoning")] long Reasoning,
    [property: JsonPropertyName("cache_read")] long CacheRead,
    [property: JsonPropertyName("cache_write")] long CacheWrite,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("cost")] double Cost,
    [property: JsonPropertyName("steps")] int Steps,
    [property: JsonPropertyName("partial")] bool Partial);

/// <summary>What the finished runs of one UTC day used, as the usage log counts it (<see cref="TokenUsage.Counted"/>): all repositories together, and one repository.</summary>
public sealed record UsageToday(long All, long Repository);

public sealed partial class GitWorkspace
{
    /// <summary>Where the log is, under the workspace root (a host path, so a person can read it without going into a container). No sandbox mounts this folder.</summary>
    public const string UsageLogFile = ".usage/usage.jsonl";

    /// <summary>Only this process writes the log, and .NET's append mode does not make a write land at the end on Linux, so writers take turns.</summary>
    private static readonly object UsageLogLock = new();

    public static string UsageLogPath(string root) => $"{root.TrimEnd('/')}/{UsageLogFile}";

    /// <summary>
    /// Adds one line for a finished run: which run it was, what it was about (a repository and a ticket or pull request number, never any text from it) and
    /// the tokens its agents used. The file only grows, one line per run, and runs finishing together take turns so that their lines do not mix.
    /// </summary>
    public Task LogUsageAsync(UsageLogEntry entry, DateTimeOffset now)
    {
        var root = RequireRoot();
        var usage = entry.Usage;
        var line = JsonSerializer.Serialize(new UsageLine(
            now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), entry.WorkflowId, entry.RunId, entry.Kind, entry.Repository, entry.Number, entry.Status,
            usage.Input, usage.Output, usage.Reasoning, usage.CacheRead, usage.CacheWrite, usage.Total, usage.Cost, usage.Steps, usage.Partial));

        var path = UsageLogPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (UsageLogLock)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(bytes, 0, bytes.Length);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// What the runs that finished today (UTC) used, from the usage log: in all, and for <paramref name="repository"/>. A run that is still going is not in the log yet,
    /// so the caller adds its own. A line that cannot be read is skipped, and a run written twice counts once.
    /// </summary>
    public UsageToday UsageToday(string repository, DateTimeOffset now)
    {
        var path = UsageLogPath(RequireRoot());
        if (!File.Exists(path))
        {
            return new UsageToday(0, 0);
        }
        var day = now.UtcDateTime.ToString("yyyy-MM-dd");
        long all = 0, mine = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains($"\"time\":\"{day}", StringComparison.Ordinal))
            {
                continue; // most of the log is other days: not worth parsing
            }
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!seen.Add(root.GetProperty("run_id").GetString() ?? Guid.NewGuid().ToString()))
                {
                    continue;
                }
                var counted = root.GetProperty("input").GetInt64() + root.GetProperty("output").GetInt64()
                    + root.GetProperty("reasoning").GetInt64() + root.GetProperty("cache_write").GetInt64();
                all += counted;
                if (string.Equals(root.GetProperty("repository").GetString(), repository, StringComparison.Ordinal))
                {
                    mine += counted;
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // A line cut short by a write in progress, or not ours: skipped.
            }
        }
        return new UsageToday(all, mine);
    }
}
