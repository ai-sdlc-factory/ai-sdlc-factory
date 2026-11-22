using System.Text;
using System.Text.Json;

namespace AiSdlcFactory.Controller;

/// <summary>Reads the output of <c>opencode run --format json</c>: one JSON event per line.</summary>
public static class OpenCodeEvents
{
    /// <summary>
    /// The tokens and cost of a run: the sum over every <c>step_finish</c> event, or null if there are none. When the
    /// output was truncated the first steps may be missing, so the result is marked partial.
    /// </summary>
    public static TokenUsage? Usage(string? output, bool truncated = false)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }
        long input = 0, outputTokens = 0, reasoning = 0, cacheRead = 0, cacheWrite = 0, total = 0;
        double cost = 0;
        var steps = 0;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{'))
            {
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "step_finish"
                    || !root.TryGetProperty("part", out var part) || part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                steps++;
                if (part.TryGetProperty("cost", out var costValue) && costValue.ValueKind == JsonValueKind.Number)
                {
                    cost += costValue.GetDouble();
                }
                if (!part.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var stepInput = Number(tokens, "input");
                var stepOutput = Number(tokens, "output");
                var stepReasoning = Number(tokens, "reasoning");
                long stepCacheRead = 0, stepCacheWrite = 0;
                if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                {
                    stepCacheRead = Number(cache, "read");
                    stepCacheWrite = Number(cache, "write");
                }
                input += stepInput;
                outputTokens += stepOutput;
                reasoning += stepReasoning;
                cacheRead += stepCacheRead;
                cacheWrite += stepCacheWrite;
                var stepTotal = Number(tokens, "total");
                total += stepTotal > 0 ? stepTotal : stepInput + stepOutput + stepReasoning + stepCacheRead + stepCacheWrite;
            }
            catch (JsonException)
            {
                // Not an event line.
            }
        }
        return steps == 0 ? null : new TokenUsage(input, outputTokens, reasoning, cacheRead, cacheWrite, total, cost, steps, truncated);
    }

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : 0;

    /// <summary>
    /// The text of the last assistant message, or null if there is none. Tool calls and progress
    /// events are ignored. Lines that are not JSON are skipped, which also covers a first line cut
    /// off by output truncation.
    /// </summary>
    public static string? FinalText(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        string? lastMessage = null;
        var parts = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{'))
            {
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && root.TryGetProperty("part", out var part)
                    && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    var message = part.TryGetProperty("messageID", out var id) ? id.GetString() : null;
                    if (message != lastMessage)
                    {
                        parts.Clear();
                        lastMessage = message;
                    }
                    parts.Add(text.GetString()!);
                }
            }
            catch (JsonException)
            {
                // Not an event line.
            }
        }
        return parts.Count == 0 ? null : string.Concat(parts);
    }
}
