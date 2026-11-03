
namespace DevFoundry.Controller;

/// <summary>
/// Checks the agent's evidence in the workspace so a "covered" claim has to point at files that exist.
/// A failed check can only weaken a claim, never strengthen it.
/// </summary>
public static class EvidenceCheck
{
    public static IReadOnlyList<string> PathsToCheck(CoverageReport report) =>
        report.Criteria.SelectMany(c => c.Evidence).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Paths are passed as arguments, never spliced into the script, so they cannot inject code.</summary>
    public static IReadOnlyList<string> Command(IReadOnlyList<string> paths) =>
        ["sh", "-c", "for p in \"$@\"; do if [ -f \"$p\" ]; then echo \"found $p\"; fi; done", "sh", .. paths];

    public static HashSet<string> ParseFound(string? output) =>
        (output ?? "").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("found ", StringComparison.Ordinal))
            .Select(line => line["found ".Length..])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Keeps only evidence that exists. A covered criterion left without evidence becomes partial, a
    /// partial one becomes missing; the overall verdict is then derived again from the final statuses.
    /// </summary>
    public static CoverageReport Apply(CoverageReport report, ISet<string> existing)
    {
        var criteria = new List<CriterionCoverage>();
        foreach (var criterion in report.Criteria)
        {
            var evidence = criterion.Evidence.Where(existing.Contains).ToList();
            var status = criterion.Status;
            var note = criterion.Note;
            if (status != CoverageStatus.Missing && evidence.Count == 0)
            {
                status = status == CoverageStatus.Covered ? CoverageStatus.Partial : CoverageStatus.Missing;
                note = string.IsNullOrEmpty(note) ? "cited files were not found" : $"{note} (cited files were not found)";
            }
            criteria.Add(criterion with { Status = status, Evidence = evidence, Note = note });
        }
        return report with { Verdict = CoverageVerdicts.Derive(criteria.Select(c => c.Status)), Criteria = criteria };
    }
}
