using System.Globalization;
using Temporalio.Exceptions;

namespace AiSdlcFactory.Controller;

public sealed partial class GitWorkspace
{
    /// <summary>The folder the weekly reports are saved in, next to the usage log.</summary>
    public const string ReportsFolder = ".usage/reports";

    /// <summary>
    /// Makes the report for one week from the host's usage log and the egress proxy's refusal log, and saves it as <c>.usage/reports/&lt;week&gt;.md</c> (a later run for the same week replaces it).
    /// Reading and counting only: no model is involved and nothing is sent anywhere.
    /// </summary>
    /// <summary>The window of a report as dates, or an error for a request that is not valid.</summary>
    private static (DateTimeOffset From, DateTimeOffset To) Window(ImprovementRequest request)
    {
        if (!DateOnly.TryParseExact(request.From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !DateOnly.TryParseExact(request.To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) || to <= from)
        {
            throw new ApplicationFailureException("The report's week and dates are not valid.", nonRetryable: true);
        }
        return (new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    /// <summary>
    /// What the usage log says about the pull requests of a week, so the git host can be asked about them: the repositories where a run opened one (<c>pr_opened</c> or <c>postmortem_opened</c>)
    /// that week, the kind of run for each ticket, and how many revisions each pull request has been pushed (<c>revision_pushed</c>, from the whole log). Numbers only.
    /// </summary>
    public PullScope ReadPullScope(ImprovementRequest request)
    {
        var (from, to) = Window(request);
        var log = UsageLogPath(RequireRoot());
        var week = LogReaders.Runs(log, from, to).Where(run => run.Status is "pr_opened" or "postmortem_opened").ToList();
        var revisions = LogReaders.Runs(log, DateTimeOffset.MinValue, DateTimeOffset.MaxValue)
            .Where(run => run.Kind == WorkIds.Revision && run.Status == Statuses.RevisionPushed)
            .GroupBy(run => (run.Repository, run.Number))
            .Select(group => new LoggedRevisions(group.Key.Repository, group.Key.Number, group.Count()))
            .ToList();
        return new PullScope(
            week.Select(run => run.Repository).Distinct().Order(StringComparer.Ordinal).ToList(),
            week.Select(run => new LoggedPullKind(run.Repository, run.Number, run.Kind)).ToList(),
            revisions);
    }

    public ImprovementReportResult BuildImprovementReport(ImprovementRequest request, IReadOnlyList<PullOutcome>? pullOutcomes = null)
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
        var reports = ImprovementReportBuilder.BuildAll(request.Week, from, to, runs, before, denied, !string.IsNullOrEmpty(settings.EgressDeniedLog) && File.Exists(settings.EgressDeniedLog), pullOutcomes);

        var folder = $"{root.TrimEnd('/')}/{ReportsFolder}";
        Directory.CreateDirectory(folder);
        var path = $"{folder}/{request.Week}.md";
        File.WriteAllText(path, reports.Whole.Markdown);
        SaveRepositoryReports($"{folder}/{request.Week}", reports.Repositories);
        // What is posted and read by the improver is the report without the list of repositories (see ImprovementReports).
        return new ImprovementReportResult(reports.TicketMarkdown, reports.Whole.Runs, reports.Whole.Findings.Count, path);
    }

    /// <summary>
    /// One file per repository in a folder named for the week (the week was checked to be <c>yyyy-Www</c> before this). A report made again for the same week replaces what an earlier one wrote there,
    /// including files of repositories that are no longer in it; only <c>.md</c> files in that folder are removed.
    /// </summary>
    private static void SaveRepositoryReports(string weekFolder, IReadOnlyList<RepositoryReport> repositories)
    {
        if (Directory.Exists(weekFolder))
        {
            foreach (var old in Directory.GetFiles(weekFolder, "*.md", SearchOption.TopDirectoryOnly))
            {
                File.Delete(old);
            }
        }
        if (repositories.Count == 0)
        {
            return;
        }
        Directory.CreateDirectory(weekFolder);
        foreach (var repository in repositories)
        {
            File.WriteAllText($"{weekFolder}/{repository.FileName}", repository.Report.Markdown);
        }
    }
}
