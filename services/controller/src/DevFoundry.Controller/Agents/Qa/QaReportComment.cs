using System.Text;

namespace DevFoundry.Controller;

/// <summary>How one suite ended in a QA run. <see cref="Tail"/> is the end of its output; <see cref="AppDidNotStart"/> means the application never answered.</summary>
public sealed record QaSuiteRun(string Suite, bool Passed, long ExitCode, bool TimedOut, string Tail, bool AppDidNotStart);

/// <summary>Where the tests ended up: pushed onto the pull request, saved to a branch for a person to look at, or nowhere.</summary>
public sealed record QaTestsOutcome(string Kind, string? Reference, int FileCount, string? Note)
{
    public const string Pushed = "pushed";
    public const string Saved = "saved";
    public const string None = "none";
}

/// <summary>Everything the report on the ticket is made from.</summary>
public sealed record QaReportInput(
    PullRequestDetails Pull, IReadOnlyList<QaSuiteRun> Suites, TesterReport? Tester, ReviewReport? Ux, string? UxNote,
    QaTestsOutcome Tests, int Attempts, TokenUsage? Usage, string TraceNote, string Mode = QaRequest.Focused, string? Note = null);

/// <summary>The QA report as a comment. The numbered findings under the heading are what <c>/fix qa</c> reads back, so each is one line.</summary>
public static class QaReportComment
{
    public static IReadOnlyList<string> Findings(QaReportInput input)
    {
        var findings = new List<string>();
        foreach (var run in input.Suites.Where(s => s.AppDidNotStart))
        {
            findings.Add($"[blocker] The application did not start for the {run.Suite} tests: {Line(run.Tail, 300)}");
        }
        var assessments = input.Tester?.Assessments ?? [];
        foreach (var assessment in assessments.Where(a => a.Verdict == TesterReport.ApplicationBug))
        {
            findings.Add($"[major] Test {Line(assessment.Test, 120)} fails because of the application: {Line(assessment.Explanation, 300)}");
        }
        foreach (var finding in input.Tester?.Findings ?? [])
        {
            findings.Add($"[{finding.Severity}] {Line(finding.Message, 300)} Steps: {Line(finding.Steps, 300)}");
        }
        foreach (var finding in input.Ux?.Blocking ?? [])
        {
            findings.Add($"[{finding.Severity}] UX{(finding.Path is null ? "" : $" {Line(finding.Path, 100)}:")} {Line(finding.Message, 300)}");
        }
        // A failing suite nobody explained is still something to look at.
        foreach (var run in input.Suites.Where(s => !s.Passed && !s.AppDidNotStart))
        {
            if (!assessments.Any(a => a.Verdict == TesterReport.ApplicationBug))
            {
                findings.Add($"[major] The {run.Suite} tests fail: {Line(run.Tail, 300)}");
            }
        }
        return findings.Take(QaReportFormat.MaxFindings).ToList();
    }

    public static string Build(QaReportInput input)
    {
        var text = new StringBuilder();
        text.AppendLine(QaReportFormat.Marker);
        var allPassed = input.Suites.Count > 0 && input.Suites.All(s => s.Passed);
        var findings = Findings(input);
        text.AppendLine($"**QA report for pull request #{input.Pull.Number}: {(allPassed && findings.Count == 0 ? "no problems found" : findings.Count == 0 ? "tests need a look" : $"{findings.Count} finding{(findings.Count == 1 ? "" : "s")}")}**");
        text.AppendLine();
        text.AppendLine(input.Mode == QaRequest.Full
            ? "**Scope:** the whole application and this pull request's change."
            : "**Scope:** this pull request's change.");
        if (!string.IsNullOrWhiteSpace(input.Note))
        {
            text.AppendLine($"**Your note:** {TicketComments.Inert(Clip(input.Note, 300))}");
        }
        text.AppendLine();

        if (input.Tester is { } tester)
        {
            text.AppendLine(TicketComments.Inert(Clip(tester.Summary, 1200)));
            text.AppendLine();
        }

        text.AppendLine("**Test runs** (run by the factory, not by an agent)");
        text.AppendLine();
        foreach (var run in input.Suites)
        {
            var state = run.AppDidNotStart ? "the application did not start"
                : run.Passed ? "passed"
                : run.TimedOut ? "timed out"
                : $"failed (exit code {run.ExitCode})";
            var scenarios = input.Tester?.Scenarios.Count(s => s.Suite == run.Suite) ?? 0;
            text.AppendLine($"- {run.Suite}: {state}{(scenarios > 0 ? $", {scenarios} scenario{(scenarios == 1 ? "" : "s")}" : "")}");
        }
        if (input.Suites.Count == 0)
        {
            text.AppendLine("- no suite ran");
        }

        if (input.Tester is { Scenarios.Count: > 0 } written)
        {
            text.AppendLine();
            text.AppendLine("**Scenarios**");
            text.AppendLine();
            foreach (var scenario in written.Scenarios.Take(20))
            {
                var covers = scenario.Covers.Count > 0 ? $" (criteria {string.Join(", ", scenario.Covers)})" : "";
                text.AppendLine($"- {scenario.Suite}: {TicketComments.Inert(Clip(scenario.Name, 200))}{covers}");
            }
        }

        text.AppendLine();
        text.AppendLine("**Tests**");
        text.AppendLine();
        text.AppendLine(input.Tests.Kind switch
        {
            QaTestsOutcome.Pushed => $"The tests passed and were added to the pull request in one commit (`{TicketComments.Inert(Clip(input.Tests.Reference ?? "", 40))}`, {input.Tests.FileCount} file(s)).",
            QaTestsOutcome.Saved => $"The tests were **not** added to the pull request. {input.Tests.FileCount} file(s) are saved on the branch `{TicketComments.Inert(Clip(input.Tests.Reference ?? "", 120))}` for a person to look at.{(input.Tests.Note is null ? "" : " " + TicketComments.Inert(Clip(input.Tests.Note, 400)))}",
            _ => $"No tests were added. {TicketComments.Inert(Clip(input.Tests.Note ?? "", 300))}".TrimEnd(),
        });

        if (input.Ux is { } ux)
        {
            text.AppendLine();
            text.AppendLine($"**UX review: {(!ux.Blocking.Any() ? "no blocking findings" : "changes needed")}**");
            text.AppendLine();
            text.AppendLine(TicketComments.Inert(Clip(ux.Summary, 800)));
            foreach (var advice in ux.Advice.Take(4))
            {
                text.AppendLine($"- {advice.Severity}{(advice.Path is null ? "" : $" {TicketComments.Inert(Clip(advice.Path, 100))}")}: {TicketComments.Inert(Clip(advice.Message, 240))}");
            }
        }
        else if (input.UxNote is not null)
        {
            text.AppendLine();
            text.AppendLine($"**UX review:** {TicketComments.Inert(Clip(input.UxNote, 300))}");
        }

        if (findings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(QaReportFormat.FindingsHeading);
            text.AppendLine();
            for (var i = 0; i < findings.Count; i++)
            {
                text.AppendLine($"{i + 1}. {findings[i]}");
            }
            text.AppendLine();
            text.AppendLine("Findings from the tester are its own observations; a test that fails is a fact, but it can still be a wrong test. Look before you act. " +
                $"To have the developer address them in this pull request, comment `/fix qa` on pull request #{input.Pull.Number}.");
        }

        text.AppendLine();
        text.AppendLine($"{input.Attempts} attempt{(input.Attempts == 1 ? "" : "s")} of the tester. {input.TraceNote}".TrimEnd());
        return text.ToString().TrimEnd();
    }

    private static string Clip(string text, int max) => text.Length > max ? text[..max] + "..." : text;

    private static string Line(string text, int max) => TicketComments.Inert(Clip(text.Replace("\r", " ").Replace("\n", " "), max));
}
