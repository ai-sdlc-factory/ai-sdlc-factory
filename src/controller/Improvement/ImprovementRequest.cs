namespace AiSdlcFactory.Controller;

/// <summary>
/// One weekly evaluation: the ISO week to look at (<see cref="Week"/>, <c>2026-W46</c>) as the dates it covers (<see cref="From"/> to <see cref="To"/>, the end not included, <c>yyyy-MM-dd</c> in UTC),
/// and the repository whose ticket carries the result (<c>owner/name</c>, or null to only save the report).
/// </summary>
public sealed record ImprovementRequest(string Week, string From, string To, string? Repository)
{
    /// <summary>The Temporal id: one per week, so a restart of the controller never makes a second report for the same week.</summary>
    public string WorkflowId => $"improvement-{Week.ToLowerInvariant()}";

    /// <summary>The week that ended before <paramref name="now"/>: Monday to the next Monday, UTC.</summary>
    public static ImprovementRequest ForLastWeek(DateTimeOffset now, string? repository)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var from = thisMonday.AddDays(-7);
        var label = $"{System.Globalization.ISOWeek.GetYear(from.ToDateTime(TimeOnly.MinValue))}-W{System.Globalization.ISOWeek.GetWeekOfYear(from.ToDateTime(TimeOnly.MinValue)):00}";
        return new ImprovementRequest(label, from.ToString("yyyy-MM-dd"), thisMonday.ToString("yyyy-MM-dd"), string.IsNullOrWhiteSpace(repository) ? null : repository);
    }
}

/// <summary>The weekly report as it was made and saved. <see cref="Path"/> is on the host (inside the workspace root).</summary>
public sealed record ImprovementReportResult(string Markdown, int Runs, int Findings, string Path)
{
    public bool HasFindings => Findings > 0;
}
