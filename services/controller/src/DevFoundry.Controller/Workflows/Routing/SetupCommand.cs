using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>
/// The <c>/setup</c> command: a line that is only <c>/setup</c> in a comment on a ticket (the ticket needs no factory label). It asks the factory to propose a
/// <c>.factory/project.json</c> for a repository that has none. Anything after the word on the line is ignored, so a longer sentence is not mistaken for the command.
/// </summary>
public static partial class SetupCommand
{
    [GeneratedRegex(@"^[ \t]*/setup[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Line();

    public static bool Matches(string body) => Line().IsMatch(body);
}
