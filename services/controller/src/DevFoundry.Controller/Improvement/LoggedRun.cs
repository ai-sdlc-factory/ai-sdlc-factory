using System.Text.Json;

namespace DevFoundry.Controller;

/// <summary>One finished run, as the usage log has it. Older lines have no <see cref="Roles"/>, rounds or check failures: they read as none.</summary>
public sealed record LoggedRun(
    DateTimeOffset Time, string WorkflowId, string RunId, string Kind, string Repository, int Number, string Status,
    long Counted, int Rounds, int GateFailures, IReadOnlyDictionary<string, RoleStats> Roles);

/// <summary>One refusal of the egress proxy.</summary>
public sealed record DeniedHost(DateTimeOffset Time, string Profile, string Host, string Reason);

/// <summary>Reads the two host-side logs the weekly report is made from. A line that cannot be read is skipped; a missing file is an empty log.</summary>
public static class LogReaders
{
    /// <summary>The runs that finished in [<paramref name="from"/>, <paramref name="to"/>), each once (the last line of a run id wins).</summary>
    public static IReadOnlyList<LoggedRun> Runs(string path, DateTimeOffset from, DateTimeOffset to)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        var runs = new Dictionary<string, LoggedRun>(StringComparer.Ordinal);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var time = DateTimeOffset.Parse(root.GetProperty("time").GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
                if (time < from || time >= to)
                {
                    continue;
                }
                var roles = new Dictionary<string, RoleStats>(StringComparer.Ordinal);
                if (root.TryGetProperty("roles", out var rolesValue) && rolesValue.ValueKind == JsonValueKind.Object)
                {
                    foreach (var role in rolesValue.EnumerateObject())
                    {
                        roles[role.Name] = new RoleStats(Int(role.Value, "attempts"), Int(role.Value, "rejected"), Int(role.Value, "failed"), Long(role.Value, "tokens"));
                    }
                }
                var runId = root.GetProperty("run_id").GetString();
                runs[string.IsNullOrEmpty(runId) ? $"line-{number}" : runId] = new LoggedRun(
                    time, root.GetProperty("workflow_id").GetString() ?? "", runId ?? "", root.GetProperty("kind").GetString() ?? "",
                    root.GetProperty("repository").GetString() ?? "", Int(root, "number"), root.GetProperty("status").GetString() ?? "",
                    Long(root, "input") + Long(root, "output") + Long(root, "reasoning") + Long(root, "cache_write"),
                    Int(root, "rounds"), Int(root, "gate_failures"), roles);
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // A line cut short by a write in progress, or not ours.
            }
        }
        return runs.Values.OrderBy(run => run.Time).ToList();
    }

    /// <summary>The refusals in [<paramref name="from"/>, <paramref name="to"/>), from the egress proxy's refusal log (one JSON line each).</summary>
    public static IReadOnlyList<DeniedHost> Denied(string path, DateTimeOffset from, DateTimeOffset to)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return [];
        }
        var denied = new List<DeniedHost>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var time = root.GetProperty("time").GetDateTimeOffset();
                if (time < from || time >= to || root.GetProperty("decision").GetString() != "deny")
                {
                    continue;
                }
                denied.Add(new DeniedHost(time, root.GetProperty("profile").GetString() ?? "?", root.GetProperty("host").GetString() ?? "?", root.GetProperty("reason").GetString() ?? ""));
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // Skipped.
            }
        }
        return denied;
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
