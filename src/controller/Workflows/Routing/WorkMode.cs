namespace DevFoundry.Controller;

/// <summary>
/// Quick mode skips the coverage check, the design review and the acceptance check; the analyst, the developer, the project's checks and
/// the reviewers still run. Only a person chooses it (a ticket label, or the project's default); no agent can.
/// </summary>
public static class WorkMode
{
    public const string QuickLabel = "quick";

    /// <summary>A ticket with this label always gets the full process, even in a project that defaults to quick.</summary>
    public const string FullLabel = "full";

    /// <summary>Why this ticket runs in quick mode, or null when it runs the full process.</summary>
    public static string? QuickReason(IEnumerable<string>? labels, ProjectConfig project)
    {
        var names = (labels ?? []).ToList();
        bool Has(string label) => names.Any(name => string.Equals(name, label, StringComparison.OrdinalIgnoreCase));
        if (Has(FullLabel))
        {
            return null;
        }
        if (Has(QuickLabel))
        {
            return $"the ticket has the \"{QuickLabel}\" label";
        }
        return project.QuickByDefault ? "the project's default mode is quick" : null;
    }
}
