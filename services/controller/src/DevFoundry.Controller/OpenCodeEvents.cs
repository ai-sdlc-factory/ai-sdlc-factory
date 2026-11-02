using System.Text;
using System.Text.Json;

namespace DevFoundry.Controller;

/// <summary>Reads the output of <c>opencode run --format json</c>: one JSON event per line.</summary>
public static class OpenCodeEvents
{
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
