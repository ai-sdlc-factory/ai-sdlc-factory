using System.Text.RegularExpressions;

namespace DevFoundry.AlertIntake;

/// <summary>
/// Makes text from alerts and logs safe to put in a ticket that people, and the factory's agents, will read: secrets and personal addresses are
/// masked, the text is cut, and nothing in it can notify a person or break the ticket's formatting.
/// </summary>
public static partial class Redactor
{
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|authorization|credential)s?\b(\s*[=:]\s*)((?:bearer\s+)?(?:""[^""]*""|'[^']*'|\S+))")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"://[^/\s:@]+:[^/\s@]+@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Za-z0-9+/_-]{40,}={0,2}")]
    private static partial Regex OpaqueToken();

    /// <summary>One line of text for a ticket, at most <paramref name="max"/> characters.</summary>
    public static string Line(string? text, int max) => Clip(Flatten(Mask(text ?? "")), max);

    /// <summary>A block of text (a few lines), at most <paramref name="max"/> characters.</summary>
    public static string Block(string? text, int max)
    {
        var lines = Mask(text ?? "").Replace("\r", "").Split('\n').Select(line => Neutralize(line).TrimEnd());
        return Clip(string.Join('\n', lines).Trim(), max);
    }

    public static string Mask(string text)
    {
        text = UrlCredentials().Replace(text, "://[redacted]@");
        text = Bearer().Replace(text, "Bearer [redacted]");
        text = Assignment().Replace(text, "$1$2[redacted]");
        text = Email().Replace(text, "[email]");
        return OpaqueToken().Replace(text, "[redacted]");
    }

    private static string Flatten(string text) => Neutralize(text.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ")).Trim();

    /// <summary>No mention that notifies, no backtick that opens a code span, no control characters.</summary>
    private static string Neutralize(string text)
    {
        var clean = new string(text.Where(c => c == '\t' || !char.IsControl(c)).ToArray());
        return clean.Replace("@", "@​").Replace('`', '\'');
    }

    private static string Clip(string text, int max) => text.Length > max ? text[..max] + "..." : text;
}
