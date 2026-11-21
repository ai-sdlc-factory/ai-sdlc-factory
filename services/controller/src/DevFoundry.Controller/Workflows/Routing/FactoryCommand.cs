using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>
/// The <c>/factory &lt;kind&gt;</c> command: a line that is only that, with a kind from <see cref="TriggerRules.Kinds"/> (<c>/factory feature</c>), asks for that workflow on a ticket that carries
/// no label for it. It is the one trigger every host has, because every host has comments. Whether it counts is the host's choice (<see cref="TriggerRules.Command"/>).
/// </summary>
public static partial class FactoryCommand
{
    [GeneratedRegex(@"^[ \t]*/factory[ \t]+(?<kind>[a-z]+)[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Line();

    /// <summary>The kind the line asks for, or null when there is no such line or the kind is not one the factory runs.</summary>
    public static string? Parse(string body)
    {
        foreach (Match match in Line().Matches(body))
        {
            var kind = match.Groups["kind"].Value.ToLowerInvariant();
            if (TriggerRules.Kinds.Contains(kind))
            {
                return kind;
            }
        }
        return null;
    }
}
