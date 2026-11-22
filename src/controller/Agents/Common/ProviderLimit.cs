using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSdlcFactory.Controller;

/// <summary>
/// Tells an agent run that failed because the model provider's usage limit is reached (a plan's cap, a quota, rate limiting) from one that failed for any other
/// reason, so the run can stop at once with that said, instead of trying the same call again and being told the same. It looks only at where a failure is reported:
/// OpenCode's standard error and the <c>error</c> events of its output, never at what the agent itself wrote or read (a repository may well talk about rate limits).
/// It goes by the provider's wording, which no document fixes, so the patterns are kept in one place and a message it does not know is reported as an ordinary failure.
/// </summary>
public static partial class ProviderLimit
{
    [GeneratedRegex(
        """usage[ _-]?limit|rate[ _-]?limit|too many requests|insufficient[ _-]?quota|exceeded your (?:current )?(?:quota|usage|plan)|quota (?:exceeded|reached)|(?:hit|reached) (?:your|the) (?:\w+ )?limit|\bHTTP[ /0-9.]*429\b|status(?: code)?[: =]*429\b|"(?:status|code|statusCode)"\s*:\s*429\b""",
        RegexOptions.IgnoreCase)]
    private static partial Regex Wording();

    /// <summary>A short quote of what the provider said when it is a usage limit, otherwise null. Only a failed run is looked at; the caller checks that.</summary>
    public static string? Detect(string? standardOutput, string? standardError)
    {
        var places = new List<string>();
        if (!string.IsNullOrWhiteSpace(standardError))
        {
            places.Add(standardError);
        }
        places.AddRange(ErrorEvents(standardOutput));
        foreach (var place in places)
        {
            if (Wording().Match(place) is { Success: true } match)
            {
                var start = Math.Max(0, match.Index - 60);
                var length = Math.Min(place.Length - start, match.Length + 160);
                return place.Substring(start, length).Replace('\n', ' ').Replace('\r', ' ').Trim();
            }
        }
        return null;
    }

    /// <summary>Every <c>error</c> event line of OpenCode's JSON output, whole, so a nested message is found wherever it sits.</summary>
    private static List<string> ErrorEvents(string? output)
    {
        var events = new List<string>();
        if (string.IsNullOrEmpty(output))
        {
            return events;
        }
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('{') && IsErrorEvent(trimmed))
            {
                events.Add(trimmed);
            }
        }
        return events;
    }

    private static bool IsErrorEvent(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "error", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false; // a line cut off by truncation, or not an event
        }
    }
}
