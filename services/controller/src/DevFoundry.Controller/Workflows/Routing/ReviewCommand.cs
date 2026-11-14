using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>The <c>/review</c> command: a line that is just <c>/review</c>, in a comment on a pull request.</summary>
public static partial class ReviewCommand
{
    [GeneratedRegex(@"^[ \t]*/review[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Line();

    public static bool IsMatch(string body) => Line().IsMatch(body.Replace("\r", ""));
}
