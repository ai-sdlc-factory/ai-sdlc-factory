using System.Text.RegularExpressions;

namespace AiSdlcFactory.Controller;

/// <summary>
/// The <c>/ask</c> command: a line starting with <c>/ask</c> in a comment on a ticket or pull request. The question is the rest of that line and every line after it,
/// at most <see cref="MaxQuestion"/> characters. A bare <c>/ask</c> is still a command (the answer is how to use it), so it is not mistaken for a comment to an agent.
/// </summary>
public static partial class AskCommand
{
    public const int MaxQuestion = 1500;

    [GeneratedRegex(@"^[ \t]*/ask(?![A-Za-z0-9_-])(?<rest>[^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Line();

    /// <summary>The question (possibly empty), or null when the comment is not an <c>/ask</c> command.</summary>
    public static string? Parse(string body)
    {
        var match = Line().Match(body);
        if (!match.Success)
        {
            return null;
        }
        var question = (match.Groups["rest"].Value + body[(match.Index + match.Length)..]).Trim();
        return question.Length > MaxQuestion ? question[..MaxQuestion] : question;
    }
}
