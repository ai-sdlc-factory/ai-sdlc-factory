using System.Text.RegularExpressions;

namespace AiSdlcFactory.Controller;

/// <summary>The <c>/update</c> command: a line that is just <c>/update</c>, in a comment on a ticket or on the factory's pull request.</summary>
public static partial class UpdateCommand
{
    [GeneratedRegex(@"^[ \t]*/update[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Line();

    public static bool IsMatch(string body) => Line().IsMatch(body.Replace("\r", ""));
}
