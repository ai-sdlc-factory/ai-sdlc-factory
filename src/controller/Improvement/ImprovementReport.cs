using System.Globalization;
using System.Text;

namespace AiSdlcFactory.Controller;

/// <summary>One thing that repeated: where (the area), what (the subject) and the numbers that say so.</summary>
public sealed record ReportFinding(string Area, string Subject, string Evidence);

/// <param name="Markdown">The whole report, as it is saved and posted.</param>
/// <param name="Findings">What repeated, flagged by the rules in <see cref="ReportRules"/>; empty when nothing did.</param>
public sealed record ImprovementReport(string Week, int Runs, IReadOnlyList<ReportFinding> Findings, string Markdown)
{
    public bool HasFindings => Findings.Count > 0;
}

/// <summary>
/// What counts as "repeating" in the weekly report, in one place. All of it is counting: the same data always gives the same report, no model is involved, and a rule only
/// fires when there are enough runs or attempts to mean something (<see cref="MinSample"/>), so one bad week of two runs says nothing.
/// </summary>
public static class ReportRules
{
    /// <summary>Fewer attempts or runs than this are shown but never flagged.</summary>
    public const int MinSample = 5;

    /// <summary>A role whose answers were rejected, or whose runs failed, at least this share of the time (percent).</summary>
    public const int RejectedOrFailedPercent = 20;

    /// <summary>A kind of run where at least this share of the runs (percent) needed this many rounds or more.</summary>
    public const int ManyRoundsPercent = 30;
    public const int ManyRounds = 3;

    /// <summary>The same kind of run ending the same way (not a healthy ending) this many times.</summary>
    public const int SameEndingCount = 3;

    /// <summary>A run is a token outlier when it used at least this many times the median of its kind (needs <see cref="MinSample"/> runs of the kind) and at least <see cref="OutlierFloor"/> tokens.</summary>
    public const int OutlierTimesMedian = 3;
    public const long OutlierFloor = 50_000;

    /// <summary>At least this share of a week's pull requests (percent), with at least <see cref="MinSample"/> of them, were closed without merging: the work was turned down.</summary>
    public const int ClosedUnmergedPercent = 30;

    /// <summary>The merged pull requests of the week needed this many revisions or more on average (with at least <see cref="MinSample"/> merged): what the factory opens is not right the first time.</summary>
    public const double ManyRevisionsAverage = 2;

    /// <summary>From this many pull requests in the week the section shows percentages; with fewer it shows counts and says that too few were opened to say more.</summary>
    public const int PercentagesFrom = 20;

    /// <summary>The same host refused this many times.</summary>
    public const int RepeatedRefusals = 3;

    /// <summary>Endings that need no attention. Any other status is trouble: something stopped, failed or needed a person.</summary>
    public static readonly HashSet<string> HealthyEndings =
    [
        "pr_opened", "postmortem_opened", "plan_created", "qa_reported", "revision_pushed", "revision_replied", "update_pushed", "up_to_date",
        "review_posted", "answered", "closed", "already_implemented", "no_changes", "improvement_posted", "report_saved",
    ];
}

/// <summary>Builds the weekly report from the usage log's runs and the egress proxy's refusals. Pure: the same input gives the same text.</summary>
public static class ImprovementReportBuilder
{
    public static ImprovementReport Build(string week, DateOnly from, DateOnly to, IReadOnlyList<LoggedRun> runs, IReadOnlyList<LoggedRun> previousWeek, IReadOnlyList<DeniedHost> denied, bool deniedLogExists, IReadOnlyList<PullOutcome>? pullOutcomes = null)
    {
        var findings = new List<ReportFinding>();
        var text = new StringBuilder();
        text.AppendLine($"# Factory report, {week} ({from:yyyy-MM-dd} to {to:yyyy-MM-dd})");
        text.AppendLine();
        text.AppendLine("Made by the factory from its own run log and the egress proxy's refusal log: counts only, nothing the tickets said. The rules that flag something are in `ReportRules.cs`. **Nothing in this report is applied by the factory.**");
        text.AppendLine();

        AppendSummary(text, runs, previousWeek);
        var roleTotals = RoleTotals(runs);
        AppendRoles(text, findings, roleTotals);
        AppendRounds(text, findings, runs);
        AppendEndings(text, findings, runs);
        AppendPullRequests(text, findings, pullOutcomes);
        AppendOutliers(text, findings, runs);
        AppendRefusals(text, findings, denied, deniedLogExists);

        // The findings are shown first, because they are what the rest is for.
        var summary = new StringBuilder();
        summary.AppendLine(findings.Count == 0
            ? "## What repeated\n\nNothing repeated enough to flag this week."
            : $"## What repeated ({findings.Count})\n");
        foreach (var finding in findings)
        {
            summary.AppendLine($"- **{finding.Area}: {finding.Subject}.** {finding.Evidence}");
        }
        summary.AppendLine();
        var all = text.ToString();
        var marker = "## This week";
        var at = all.IndexOf(marker, StringComparison.Ordinal);
        var markdown = all[..at] + summary + all[at..];
        return new ImprovementReport(week, runs.Count, findings, markdown.TrimEnd() + "\n");
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(int part, int whole) => whole == 0 ? "0%" : $"{part * 100 / whole}%";

    private static string Count(IEnumerable<string> items) =>
        string.Join(", ", items.GroupBy(item => item).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Select(group => $"{group.Key} {group.Count()}"));

    private static void AppendSummary(StringBuilder text, IReadOnlyList<LoggedRun> runs, IReadOnlyList<LoggedRun> previous)
    {
        text.AppendLine("## This week");
        text.AppendLine();
        if (runs.Count == 0)
        {
            text.AppendLine("No run that used an agent finished this week.");
            text.AppendLine();
            return;
        }
        var tokens = runs.Sum(run => run.Counted);
        var before = previous.Sum(run => run.Counted);
        var change = previous.Count == 0 ? "no runs the week before" : before == 0 ? "the week before used none" : $"{(tokens >= before ? "+" : "-")}{Math.Abs(tokens - before) * 100 / before}% against the week before ({Number(before)} in {previous.Count} runs)";
        text.AppendLine($"- **{runs.Count} runs** ({Count(runs.Select(run => run.Kind))}) used **{Number(tokens)} tokens** (input, output, reasoning and cache writes): {change}.");
        text.AppendLine($"- How they ended: {Count(runs.Select(run => run.Status))}.");
        text.AppendLine($"- In {runs.Select(run => run.Repository).Distinct().Count()} repositories: {Count(runs.Select(run => run.Repository))}.");
        text.AppendLine();
    }

    private static Dictionary<string, RoleStats> RoleTotals(IReadOnlyList<LoggedRun> runs) =>
        runs.SelectMany(run => run.Roles).GroupBy(pair => pair.Key).ToDictionary(
            group => group.Key,
            group => new RoleStats(group.Sum(pair => pair.Value.Attempts), group.Sum(pair => pair.Value.Rejected), group.Sum(pair => pair.Value.Failed), group.Sum(pair => pair.Value.Tokens)));

    private static void AppendRoles(StringBuilder text, List<ReportFinding> findings, Dictionary<string, RoleStats> roles)
    {
        text.AppendLine("## Agents: rejected answers, failed runs and tokens");
        text.AppendLine();
        if (roles.Count == 0)
        {
            text.AppendLine("No per-agent figures in the runs of this week (runs logged before the log kept them have none).");
            text.AppendLine();
            return;
        }
        text.AppendLine("A rejected answer is one the factory could not use (the wrong shape, a rule broken); a failed run is one that did not answer at all. Each is sent back once, which costs tokens.");
        text.AppendLine();
        text.AppendLine("| agent | attempts | rejected | failed | tokens |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var (role, stats) in roles.OrderByDescending(pair => pair.Value.Tokens).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"| {role} | {stats.Attempts} | {stats.Rejected} ({Percent(stats.Rejected, stats.Attempts)}) | {stats.Failed} ({Percent(stats.Failed, stats.Attempts)}) | {Number(stats.Tokens)} |");
            if (stats.Attempts < ReportRules.MinSample)
            {
                continue;
            }
            if (stats.Rejected * 100 >= ReportRules.RejectedOrFailedPercent * stats.Attempts)
            {
                findings.Add(new ReportFinding("Rejected answers", $"the {role} agent",
                    $"{stats.Rejected} of its {stats.Attempts} answers ({Percent(stats.Rejected, stats.Attempts)}) were rejected. Its prompt or its answer shape may be unclear."));
            }
            if (stats.Failed * 100 >= ReportRules.RejectedOrFailedPercent * stats.Attempts)
            {
                findings.Add(new ReportFinding("Failed agent runs", $"the {role} agent",
                    $"{stats.Failed} of its {stats.Attempts} runs ({Percent(stats.Failed, stats.Attempts)}) failed outright (no answer, a timeout or an error)."));
            }
        }
        text.AppendLine();
    }

    private static void AppendRounds(StringBuilder text, List<ReportFinding> findings, IReadOnlyList<LoggedRun> runs)
    {
        text.AppendLine("## Rounds used");
        text.AppendLine();
        var withRounds = runs.Where(run => run.Rounds > 0).ToList();
        if (withRounds.Count == 0)
        {
            text.AppendLine("No run of this week used rounds (build, revision, update or documentation rounds).");
            text.AppendLine();
            return;
        }
        text.AppendLine("| kind | runs with rounds | average | at 3 or more | runs with failed checks |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var group in withRounds.GroupBy(run => run.Kind).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var many = group.Count(run => run.Rounds >= ReportRules.ManyRounds);
            text.AppendLine($"| {group.Key} | {group.Count()} | {group.Average(run => run.Rounds):0.0} | {many} ({Percent(many, group.Count())}) | {group.Count(run => run.GateFailures > 0)} |");
            if (group.Count() >= ReportRules.MinSample && many * 100 >= ReportRules.ManyRoundsPercent * group.Count())
            {
                findings.Add(new ReportFinding("Rounds used", $"{group.Key} runs",
                    $"{many} of {group.Count()} runs ({Percent(many, group.Count())}) needed {ReportRules.ManyRounds} rounds or more. They are the ones that retry what an agent got wrong the first time."));
            }
        }
        text.AppendLine();
    }

    private static void AppendEndings(StringBuilder text, List<ReportFinding> findings, IReadOnlyList<LoggedRun> runs)
    {
        text.AppendLine("## Runs that did not end well");
        text.AppendLine();
        var trouble = runs.Where(run => !ReportRules.HealthyEndings.Contains(run.Status)).ToList();
        if (trouble.Count == 0)
        {
            text.AppendLine("Every run ended in a pull request, a posted result or a close.");
            text.AppendLine();
            return;
        }
        text.AppendLine("| kind | ended as | runs |");
        text.AppendLine("| --- | --- | ---: |");
        foreach (var group in trouble.GroupBy(run => (run.Kind, run.Status)).OrderByDescending(group => group.Count()).ThenBy(group => group.Key.Kind, StringComparer.Ordinal).ThenBy(group => group.Key.Status, StringComparer.Ordinal))
        {
            text.AppendLine($"| {group.Key.Kind} | {group.Key.Status} | {group.Count()} |");
            if (group.Count() >= ReportRules.SameEndingCount)
            {
                findings.Add(new ReportFinding("Runs that stopped the same way", $"{group.Key.Kind} runs ending `{group.Key.Status}`",
                    $"{group.Count()} runs, in {group.Select(run => run.Repository).Distinct().Count()} repositories."));
            }
        }
        text.AppendLine();
    }

    private static void AppendPullRequests(StringBuilder text, List<ReportFinding> findings, IReadOnlyList<PullOutcome>? pulls)
    {
        text.AppendLine("## What happened to the pull requests");
        text.AppendLine();
        if (pulls is null)
        {
            text.AppendLine("The git host could not be asked what became of this week's pull requests, so this section is empty. The rest of the report does not depend on it.");
            text.AppendLine();
            return;
        }
        if (pulls.Count == 0)
        {
            text.AppendLine("The factory opened no pull request this week.");
            text.AppendLine();
            return;
        }
        text.AppendLine("The pull requests the factory opened this week, and what became of them when the report was made (a merged one is the work being accepted; a closed one that was not merged is the work being turned down). Counts and repository names only: no title or text of any pull request is read.");
        text.AppendLine();
        text.AppendLine("| kind | opened | merged | closed, not merged | still open | revisions per merged | days to merge |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var group in pulls.GroupBy(pull => pull.Kind).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            text.AppendLine(PullRow(group.Key, group.ToList()));
        }
        text.AppendLine(PullRow("**all**", pulls));
        text.AppendLine();

        var merged = pulls.Where(pull => pull.State == PullStates.Merged).ToList();
        var closed = pulls.Count(pull => pull.State == PullStates.ClosedUnmerged);
        var showPercent = pulls.Count >= ReportRules.PercentagesFrom;
        text.AppendLine(showPercent
            ? $"{Percent(merged.Count, pulls.Count)} merged, {Percent(closed, pulls.Count)} closed without merging, {Percent(pulls.Count - merged.Count - closed, pulls.Count)} still open."
            : $"Only {pulls.Count} pull request{(pulls.Count == 1 ? "" : "s")} this week: too few to say what share is accepted (percentages need {ReportRules.PercentagesFrom}).");
        text.AppendLine();

        if (pulls.Count >= ReportRules.MinSample && closed * 100 >= ReportRules.ClosedUnmergedPercent * pulls.Count)
        {
            findings.Add(new ReportFinding("Pull requests turned down", "factory pull requests",
                $"{closed} of {pulls.Count} pull requests ({Percent(closed, pulls.Count)}) were closed without being merged: the work was rejected, whatever the checks said."));
        }
        if (merged.Count >= ReportRules.MinSample && merged.Average(pull => pull.Revisions) >= ReportRules.ManyRevisionsAverage)
        {
            findings.Add(new ReportFinding("Pull requests needing revisions", "merged factory pull requests",
                $"The {merged.Count} merged pull requests needed {merged.Average(pull => pull.Revisions):0.0} revisions on average: they were not right the first time."));
        }
    }

    private static string PullRow(string name, IReadOnlyList<PullOutcome> pulls)
    {
        var merged = pulls.Where(pull => pull.State == PullStates.Merged).ToList();
        var revisions = merged.Count == 0 ? "-" : $"{merged.Average(pull => pull.Revisions):0.0}";
        var days = merged.Where(pull => pull.DaysToMerge is not null).ToList();
        var daysText = days.Count == 0 ? "-" : $"{days.Average(pull => pull.DaysToMerge!.Value):0.0}";
        return $"| {name} | {pulls.Count} | {merged.Count} | {pulls.Count(pull => pull.State == PullStates.ClosedUnmerged)} | {pulls.Count(pull => pull.State == PullStates.Open)} | {revisions} | {daysText} |";
    }

    private static void AppendOutliers(StringBuilder text, List<ReportFinding> findings, IReadOnlyList<LoggedRun> runs)
    {
        text.AppendLine("## Token outliers");
        text.AppendLine();
        var outliers = new List<(LoggedRun Run, long Median)>();
        foreach (var group in runs.GroupBy(run => run.Kind))
        {
            if (group.Count() < ReportRules.MinSample)
            {
                continue;
            }
            var sorted = group.Select(run => run.Counted).Order().ToList();
            var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
            outliers.AddRange(group.Where(run => run.Counted >= ReportRules.OutlierFloor && run.Counted >= ReportRules.OutlierTimesMedian * Math.Max(1, median)).Select(run => (run, median)));
        }
        if (outliers.Count == 0)
        {
            text.AppendLine($"No run used {ReportRules.OutlierTimesMedian} times or more the median of its kind (needs {ReportRules.MinSample} runs of a kind, and at least {Number(ReportRules.OutlierFloor)} tokens).");
            text.AppendLine();
            return;
        }
        text.AppendLine("| run | kind | ended as | tokens | median of its kind |");
        text.AppendLine("| --- | --- | --- | ---: | ---: |");
        foreach (var (run, median) in outliers.OrderByDescending(item => item.Run.Counted).Take(5))
        {
            text.AppendLine($"| {run.Repository}#{run.Number} | {run.Kind} | {run.Status} | {Number(run.Counted)} | {Number(median)} |");
        }
        text.AppendLine();
        var largest = outliers.OrderByDescending(item => item.Run.Counted).First();
        findings.Add(new ReportFinding("Token outliers", $"{outliers.Count} run{(outliers.Count == 1 ? "" : "s")}",
            $"The largest ({largest.Run.Repository}#{largest.Run.Number}, a {largest.Run.Kind} run) used {Number(largest.Run.Counted)} tokens, {largest.Run.Counted / Math.Max(1, largest.Median)} times the median of its kind."));
    }

    private static void AppendRefusals(StringBuilder text, List<ReportFinding> findings, IReadOnlyList<DeniedHost> denied, bool logExists)
    {
        text.AppendLine("## Hosts a sandbox was refused");
        text.AppendLine();
        if (!logExists)
        {
            text.AppendLine("No refusal log is available (the egress proxy writes one when `EGRESS_DENIED_LOG` is set, and the controller must be able to read it), so nothing is known about refused hosts.");
            text.AppendLine();
            return;
        }
        // Plain HTTP requests (the proxy only tunnels HTTPS) are not a decision about a host: build tools make hundreds of them per build, mostly .NET's certificate
        // revocation checks (ocsp.digicert.com and the like), which fail softly. Counted on one line, never flagged, so nobody is led to allow hosts that cannot be proxied anyway.
        var split = denied.ToLookup(item => item.Reason.StartsWith(PlainHttpReason, StringComparison.Ordinal));
        var plain = split[true].ToList();
        denied = split[false].ToList();
        if (denied.Count == 0 && plain.Count == 0)
        {
            text.AppendLine("No sandbox was refused a host this week.");
            text.AppendLine();
            return;
        }
        if (denied.Count == 0)
        {
            text.AppendLine("No sandbox was refused a host by the allowlists this week.");
        }
        else
        {
            AppendRefusalTable(text, findings, denied);
        }
        if (plain.Count > 0)
        {
            var hosts = plain.Where(item => item.Host != "-").GroupBy(item => item.Host).OrderByDescending(group => group.Count()).Take(5).Select(group => $"`{group.Key}` ({group.Count()})");
            text.AppendLine();
            text.AppendLine($"Not counted above: **{plain.Count} plain HTTP request{(plain.Count == 1 ? "" : "s")}** were refused because the proxy only carries HTTPS tunnels" +
                (hosts.Any() ? $", most to {string.Join(", ", hosts)}" : "") +
                ". Build tools do this (often certificate revocation checks, which fail softly); it needs no change unless a build stops working.");
        }
        text.AppendLine();
    }

    /// <summary>The reason the proxy gives for a plain HTTP request; see <c>ProxyServer</c> in the egress proxy.</summary>
    private const string PlainHttpReason = "only CONNECT is supported";

    private static void AppendRefusalTable(StringBuilder text, List<ReportFinding> findings, IReadOnlyList<DeniedHost> denied)
    {
        text.AppendLine("| host | profile | refused | why |");
        text.AppendLine("| --- | --- | ---: | --- |");
        foreach (var group in denied.GroupBy(item => (item.Host, item.Profile)).OrderByDescending(group => group.Count()).ThenBy(group => group.Key.Host, StringComparer.Ordinal).Take(10))
        {
            var reason = group.First().Reason.Replace('|', '/').Replace('\n', ' ');
            text.AppendLine($"| {group.Key.Host} | {group.Key.Profile} | {group.Count()} | {(reason.Length > 120 ? reason[..120] + "..." : reason)} |");
            if (group.Count() >= ReportRules.RepeatedRefusals)
            {
                findings.Add(new ReportFinding("Refused hosts", $"`{group.Key.Host}` on the {group.Key.Profile} profile", $"refused {group.Count()} times. If agents or builds need it, it belongs on the profile's list; if not, the prompt may be sending them there."));
            }
        }
        text.AppendLine();
    }
}
