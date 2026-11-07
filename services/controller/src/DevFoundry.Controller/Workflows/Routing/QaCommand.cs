using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>
/// The <c>/qa</c> command: <c>/qa [full] [api|ui] [what to take into account]</c>. A line starting with <c>/qa</c> begins it; the words
/// <c>full</c> and <c>api</c>/<c>ui</c> (in either order) are read first, and whatever follows, on that line and below it, is the person's note.
/// </summary>
public static partial class QaCommand
{
    public const int MaxNote = 1500;

    [GeneratedRegex(@"^[ \t]*/qa(?=[ \t\r\n]|$)(?<rest>[^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Line();

    [GeneratedRegex(@"\A[ \t]*(?<word>full|api|ui)(?=[ \t\r\n]|\z)[ \t]*", RegexOptions.IgnoreCase)]
    private static partial Regex Keyword();

    /// <summary>The command's parts, or null when the comment has no <c>/qa</c> line.</summary>
    public static (string Mode, string? Suite, string? Note)? Parse(string body)
    {
        if (Line().Match(body) is not { Success: true } match)
        {
            return null;
        }
        var text = (match.Groups["rest"].Value + body[(match.Index + match.Length)..]).TrimStart(' ', '\t');
        var mode = QaRequest.Focused;
        string? suite = null;
        var sawMode = false;
        while (Keyword().Match(text) is { Success: true } word)
        {
            var value = word.Groups["word"].Value.ToLowerInvariant();
            if (value == "full" && !sawMode)
            {
                mode = QaRequest.Full;
                sawMode = true;
            }
            else if (value != "full" && suite is null)
            {
                suite = value;
            }
            else
            {
                break; // a repeated keyword is part of the note
            }
            text = text[word.Length..];
        }
        var note = text.Trim();
        if (note.Length > MaxNote)
        {
            note = note[..MaxNote];
        }
        return (mode, suite, note.Length == 0 ? null : note);
    }
}
