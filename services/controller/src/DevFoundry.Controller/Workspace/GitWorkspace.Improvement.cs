using System.Globalization;
using Temporalio.Exceptions;

namespace DevFoundry.Controller;

public sealed partial class GitWorkspace
{
    /// <summary>The folder the weekly reports are saved in, next to the usage log.</summary>
    public const string ReportsFolder = ".usage/reports";

    /// <summary>
    /// Makes the report for one week from the host's usage log and the egress proxy's refusal log, and saves it as <c>.usage/reports/&lt;week&gt;.md</c> (a later run for the same week replaces it).
    /// Reading and counting only: no model is involved and nothing is sent anywhere.
    /// </summary>
    public ImprovementReportResult BuildImprovementReport(ImprovementRequest request)
    {
        var root = RequireRoot();
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Week, @"\A\d{4}-W\d{2}\z")
            || !DateOnly.TryParseExact(request.From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !DateOnly.TryParseExact(request.To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) || to <= from)
        {
            throw new ApplicationFailureException("The report's week and dates are not valid.", nonRetryable: true);
        }
        DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var length = to.DayNumber - from.DayNumber;
        var log = UsageLogPath(root);
        var runs = LogReaders.Runs(log, Start(from), Start(to));
        var before = LogReaders.Runs(log, Start(from.AddDays(-length)), Start(from));
        var denied = LogReaders.Denied(settings.EgressDeniedLog, Start(from), Start(to));
        var report = ImprovementReportBuilder.Build(request.Week, from, to, runs, before, denied, !string.IsNullOrEmpty(settings.EgressDeniedLog) && File.Exists(settings.EgressDeniedLog));

        var folder = $"{root.TrimEnd('/')}/{ReportsFolder}";
        Directory.CreateDirectory(folder);
        var path = $"{folder}/{request.Week}.md";
        File.WriteAllText(path, report.Markdown);
        return new ImprovementReportResult(report.Markdown, report.Runs, report.Findings.Count, path);
    }
}
