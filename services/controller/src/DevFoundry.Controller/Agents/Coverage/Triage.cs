namespace DevFoundry.Controller;

public sealed record TriageDecision(bool NeedsArchitect, IReadOnlyList<string> Reasons);

/// <summary>
/// Decides, with fixed rules over the analyst's facts, whether a design review comes before building.
/// No model is involved, so the same ticket always gets the same answer and every reason is stated.
/// </summary>
public static class Triage
{
    /// <summary>More files than this is treated as a change big enough to need a design.</summary>
    public const int MaxFilesWithoutArchitect = 5;

    /// <summary>
    /// A change to a public API alone, touching this many files or fewer, is small enough to build without a design review: one more route or one more field.
    /// A larger API change, and any change that also touches the data model or adds a dependency, still gets one.
    /// </summary>
    public const int MaxFilesForSmallApiChange = 2;

    /// <summary>Label a person adds to force a design review.</summary>
    public const string ForceLabel = "needs-architecture";

    public static TriageDecision Decide(Complexity complexity, IEnumerable<string> ticketLabels)
    {
        var reasons = new List<string>();
        if (ticketLabels.Any(label => string.Equals(label, ForceLabel, StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add($"the ticket has the \"{ForceLabel}\" label");
        }
        if (complexity.FilesEstimate > MaxFilesWithoutArchitect)
        {
            reasons.Add($"about {complexity.FilesEstimate} files are affected (more than {MaxFilesWithoutArchitect})");
        }
        if (complexity.MultipleProjects)
        {
            reasons.Add("more than one project is affected");
        }
        if (complexity.PublicApiChange && complexity.FilesEstimate > MaxFilesForSmallApiChange)
        {
            reasons.Add($"a public API changes and about {complexity.FilesEstimate} files are affected (more than {MaxFilesForSmallApiChange})");
        }
        if (complexity.DataModelChange)
        {
            reasons.Add("the data model changes");
        }
        if (complexity.NewDependency)
        {
            reasons.Add("a new dependency is needed");
        }
        return new TriageDecision(reasons.Count > 0, reasons);
    }
}
