namespace DevFoundry.Controller;

/// <summary>
/// The acceptance criteria the factory wrote into the pull request body (under "## Acceptance criteria") when it opened it, read back so a
/// larger revision can be checked against what the ticket asked for. Text written by people or an agent: clipped, and only plain list items.
/// </summary>
public static class PullRequestCriteria
{
    public const int MaxCriteria = 20;
    public const int MaxLength = 240;

    public static IReadOnlyList<CriterionCoverage> FromBody(string? body)
    {
        var criteria = new List<CriterionCoverage>();
        var inSection = false;
        foreach (var raw in (body ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = line.Equals("## Acceptance criteria", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || !line.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }
            var text = line[2..].Trim();
            if (text.Length == 0 || criteria.Count >= MaxCriteria)
            {
                continue;
            }
            criteria.Add(new CriterionCoverage(criteria.Count + 1, text.Length > MaxLength ? text[..MaxLength] : text, CoverageStatus.Missing, [], ""));
        }
        return criteria;
    }

    /// <summary>A revision touching this many files or more is large enough to check against the original criteria.</summary>
    public const int QaFromChangedFiles = 3;
}
