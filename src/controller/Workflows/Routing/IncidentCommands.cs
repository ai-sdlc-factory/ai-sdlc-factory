using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>
/// What a person can tell the incident workflow by writing a line that starts with a slash command. Plain, fixed
/// commands (not a model's reading of free text) decide what the factory does next.
/// </summary>
public abstract record IncidentCommand
{
    public sealed record Option(int Number) : IncidentCommand;

    public sealed record Hotfix : IncidentCommand;

    public sealed record Resolved : IncidentCommand;
}

public static partial class IncidentCommands
{
    [GeneratedRegex(@"^\s*/(?<name>option|choose|hotfix|resolved)\b(?:[ \t]+(?<arg>\d{1,3}))?[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Line();

    /// <summary>The commands in a comment, in the order they are carried out: options, then hotfix, then resolved.</summary>
    public static IReadOnlyList<IncidentCommand> Parse(string? body)
    {
        var commands = new List<IncidentCommand>();
        foreach (Match match in Line().Matches(body ?? ""))
        {
            var name = match.Groups["name"].Value.ToLowerInvariant();
            switch (name)
            {
                case "option" or "choose" when int.TryParse(match.Groups["arg"].Value, out var number):
                    commands.Add(new IncidentCommand.Option(number));
                    break;
                case "hotfix":
                    commands.Add(new IncidentCommand.Hotfix());
                    break;
                case "resolved":
                    commands.Add(new IncidentCommand.Resolved());
                    break;
            }
        }
        return commands
            .OrderBy(command => command switch { IncidentCommand.Option => 0, IncidentCommand.Hotfix => 1, _ => 2 })
            .ToList();
    }
}
