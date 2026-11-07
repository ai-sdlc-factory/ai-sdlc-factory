using System.Text.Json;

namespace DevFoundry.AlertIntake;

/// <summary>Recent log lines for a query, newest last. Nothing here is trusted: the caller redacts and cuts them.</summary>
public interface ILogSource
{
    Task<IReadOnlyList<string>> RecentLinesAsync(string query, TimeSpan window, int maxLines, CancellationToken cancellationToken);
}

/// <summary>Used when no Loki address is set: tickets then carry no log evidence.</summary>
public sealed class NoLogs : ILogSource
{
    public Task<IReadOnlyList<string>> RecentLinesAsync(string query, TimeSpan window, int maxLines, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

public sealed class LokiClient(HttpClient http, TimeProvider clock) : ILogSource
{
    public async Task<IReadOnlyList<string>> RecentLinesAsync(string query, TimeSpan window, int maxLines, CancellationToken cancellationToken)
    {
        var end = clock.GetUtcNow();
        var url = $"loki/api/v1/query_range?query={Uri.EscapeDataString(query)}&limit={maxLines}&direction=backward" +
                  $"&start={ToNanoseconds(end - window)}&end={ToNanoseconds(end)}";
        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Loki refused the log query: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var lines = new List<(long Time, string Line)>();
        if (document.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in result.EnumerateArray())
            {
                if (!stream.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 2
                        && long.TryParse(value[0].GetString(), out var time) && value[1].GetString() is { } line)
                    {
                        lines.Add((time, line));
                    }
                }
            }
        }
        return lines.OrderBy(entry => entry.Time).TakeLast(maxLines).Select(entry => entry.Line).ToList();
    }

    private static long ToNanoseconds(DateTimeOffset time) => time.ToUnixTimeMilliseconds() * 1_000_000;
}
