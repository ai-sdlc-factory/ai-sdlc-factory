namespace AiSdlcFactory.Controller.Tests;

/// <summary>The weekly report: counting only, the same data always giving the same text, and a rule firing only when there is enough to mean something.</summary>
public sealed class ImprovementReportTests
{
    private static readonly DateOnly From = new(2026, 11, 9);
    private static readonly DateOnly To = new(2026, 11, 16);

    private static LoggedRun Run(
        string kind = "feature", string status = "pr_opened", long tokens = 10_000, string repository = "acme/shop", int number = 1,
        int rounds = 1, int gateFailures = 0, params (string Role, RoleStats Stats)[] roles) =>
        new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero), $"{kind}-{repository}-{number}", Guid.NewGuid().ToString("N"), kind, repository, number, status, tokens, rounds, gateFailures,
            roles.ToDictionary(pair => pair.Role, pair => pair.Stats));

    private static ImprovementReport Build(IReadOnlyList<LoggedRun> runs, IReadOnlyList<LoggedRun>? previous = null, IReadOnlyList<DeniedHost>? denied = null, bool deniedLog = true) =>
        ImprovementReportBuilder.Build("2026-W46", From, To, runs, previous ?? [], denied ?? [], deniedLog);

    private static string[] FindingAreas(ImprovementReport report) => report.Findings.Select(finding => finding.Area).ToArray();

    [Fact]
    public void A_quiet_healthy_week_has_nothing_to_flag_and_says_so()
    {
        var report = Build([Run(), Run(number: 2)]);

        Assert.False(report.HasFindings);
        Assert.Equal(2, report.Runs);
        Assert.Contains("# Factory report, 2026-W46 (2026-11-09 to 2026-11-16)", report.Markdown);
        Assert.Contains("Nothing repeated enough to flag this week.", report.Markdown);
        Assert.Contains("**Nothing in this report is applied by the factory.**", report.Markdown);
        Assert.Contains("**2 runs** (feature 2) used **20,000 tokens**", report.Markdown);
        Assert.Contains("How they ended: pr_opened 2.", report.Markdown);
    }

    [Fact]
    public void A_week_with_no_runs_says_so_and_flags_nothing()
    {
        var report = Build([]);

        Assert.False(report.HasFindings);
        Assert.Contains("No run that used an agent finished this week.", report.Markdown);
    }

    [Fact]
    public void The_same_data_always_gives_the_same_report()
    {
        var runs = new[] { Run(status: "build_failed", number: 1), Run(status: "build_failed", number: 2), Run(status: "build_failed", number: 3), Run(tokens: 30_000, number: 4) };

        Assert.Equal(Build(runs).Markdown, Build(runs.Reverse().ToArray()).Markdown);
    }

    // ---- agents ---------------------------------------------------------------------------------------------

    [Fact]
    public void An_agent_whose_answers_are_often_rejected_is_flagged_with_the_numbers()
    {
        var report = Build([Run(roles: [("analyst", new RoleStats(10, 3, 0, 5000))]), Run(number: 2, roles: [("analyst", new RoleStats(10, 2, 0, 5000))])]);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("Rejected answers", finding.Area);
        Assert.Equal("the analyst agent", finding.Subject);
        Assert.Contains("5 of its 20 answers (25%) were rejected", finding.Evidence);
        Assert.Contains("| analyst | 20 | 5 (25%) | 0 (0%) | 10,000 |", report.Markdown);
    }

    [Fact]
    public void An_agent_whose_runs_fail_is_flagged_separately()
    {
        var report = Build([Run(roles: [("developer", new RoleStats(10, 0, 4, 90_000))])]);

        Assert.Equal(["Failed agent runs"], FindingAreas(report));
    }

    [Fact]
    public void Too_few_attempts_are_shown_but_never_flagged()
    {
        var report = Build([Run(roles: [("analyst", new RoleStats(4, 4, 4, 100))])]);

        Assert.False(report.HasFindings);
        Assert.Contains("| analyst | 4 | 4 (100%) | 4 (100%) | 100 |", report.Markdown);
    }

    [Fact]
    public void A_rate_just_under_the_line_is_not_flagged()
    {
        var report = Build([Run(roles: [("analyst", new RoleStats(10, 1, 1, 100))]), Run(number: 2, roles: [("analyst", new RoleStats(10, 1, 1, 100))])]);

        Assert.False(report.HasFindings); // 2 of 20 is 10%
    }

    [Fact]
    public void Roles_are_added_up_across_runs_and_listed_by_tokens()
    {
        var report = Build([
            Run(roles: [("developer", new RoleStats(2, 0, 0, 90_000)), ("reviewer", new RoleStats(2, 0, 0, 1_000))]),
            Run(number: 2, roles: [("developer", new RoleStats(1, 0, 0, 10_000))])]);

        var table = report.Markdown;
        Assert.Contains("| developer | 3 | 0 (0%) | 0 (0%) | 100,000 |", table);
        Assert.True(table.IndexOf("| developer |", StringComparison.Ordinal) < table.IndexOf("| reviewer |", StringComparison.Ordinal));
    }

    [Fact]
    public void Runs_logged_before_the_log_kept_per_agent_figures_are_said_to_have_none()
    {
        var report = Build([Run(), Run(number: 2)]);

        Assert.Contains("No per-agent figures in the runs of this week", report.Markdown);
    }

    // ---- rounds ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_kind_of_run_that_often_needs_many_rounds_is_flagged()
    {
        var runs = Enumerable.Range(1, 5).Select(i => Run(number: i, rounds: i <= 2 ? 3 : 1)).ToList(); // 2 of 5 = 40%

        var report = Build(runs);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(("Rounds used", "feature runs"), (finding.Area, finding.Subject));
        Assert.Contains("2 of 5 runs (40%) needed 3 rounds or more", finding.Evidence);
        Assert.Contains("| feature | 5 | 1.8 | 2 (40%) | 0 |", report.Markdown);
    }

    [Fact]
    public void Few_runs_with_many_rounds_are_not_enough_to_flag()
    {
        var report = Build(Enumerable.Range(1, 4).Select(i => Run(number: i, rounds: 3)).ToList());

        Assert.DoesNotContain("Rounds used", FindingAreas(report));
    }

    [Fact]
    public void Failed_checks_are_counted_per_kind()
    {
        var report = Build([Run(rounds: 2, gateFailures: 2), Run(number: 2, rounds: 1)]);

        Assert.Contains("| feature | 2 | 1.5 | 0 (0%) | 1 |", report.Markdown);
    }

    // ---- endings ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_same_unhealthy_ending_three_times_is_flagged_and_healthy_endings_never_are()
    {
        var report = Build([
            Run(status: "build_failed"), Run(status: "build_failed", number: 2, repository: "acme/api"), Run(status: "build_failed", number: 3),
            Run(status: "pr_opened", number: 4), Run(status: "pr_opened", number: 5), Run(status: "pr_opened", number: 6), Run(status: "pr_opened", number: 7)]);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("Runs that stopped the same way", finding.Area);
        Assert.Equal("feature runs ending `build_failed`", finding.Subject);
        Assert.Equal("3 runs, in 2 repositories.", finding.Evidence);
        Assert.Contains("| feature | build_failed | 3 |", report.Markdown);
        Assert.DoesNotContain("| feature | pr_opened |", report.Markdown);
    }

    [Fact]
    public void Two_failures_of_a_kind_are_listed_but_not_flagged()
    {
        var report = Build([Run(status: "build_failed"), Run(status: "build_failed", number: 2)]);

        Assert.False(report.HasFindings);
        Assert.Contains("| feature | build_failed | 2 |", report.Markdown);
    }

    // ---- token outliers --------------------------------------------------------------------------------------

    [Fact]
    public void A_run_far_above_the_median_of_its_kind_is_flagged_with_how_far()
    {
        var runs = new List<LoggedRun>(Enumerable.Range(1, 5).Select(i => Run(number: i, tokens: 20_000)));
        runs.Add(Run(number: 9, tokens: 200_000, status: "build_failed", repository: "acme/api"));

        var report = Build(runs);

        var finding = report.Findings.Single(f => f.Area == "Token outliers");
        Assert.Contains("acme/api#9, a feature run) used 200,000 tokens, 10 times the median of its kind", finding.Evidence);
        Assert.Contains("| acme/api#9 | feature | build_failed | 200,000 | 20,000 |", report.Markdown);
    }

    [Fact]
    public void A_large_run_among_few_or_small_ones_is_not_an_outlier()
    {
        Assert.DoesNotContain("Token outliers", FindingAreas(Build([Run(tokens: 10_000), Run(number: 2, tokens: 900_000)]))); // too few runs of the kind
        var smallRuns = Enumerable.Range(1, 5).Select(i => Run(number: i, tokens: 1_000)).Append(Run(number: 9, tokens: 40_000)).ToList();
        Assert.DoesNotContain("Token outliers", FindingAreas(Build(smallRuns))); // 40x the median, but under the floor
    }

    [Fact]
    public void Kinds_are_compared_only_with_themselves()
    {
        var runs = Enumerable.Range(1, 5).Select(i => Run(number: i, tokens: 10_000)).Concat(Enumerable.Range(1, 5).Select(i => Run(kind: "qa", number: i, tokens: 400_000, status: "qa_reported"))).ToList();

        Assert.DoesNotContain("Token outliers", FindingAreas(Build(runs)));
    }

    // ---- refused hosts ---------------------------------------------------------------------------------------

    private static DeniedHost Denied(string host = "evil.example", string profile = "developer", string reason = "host evil.example is not on the developer list") =>
        new(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero), profile, host, reason);

    [Fact]
    public void A_host_refused_again_and_again_is_flagged_with_where_it_belongs()
    {
        var report = Build([Run()], denied: [Denied(), Denied(), Denied(), Denied("once.example")]);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(("Refused hosts", "`evil.example` on the developer profile"), (finding.Area, finding.Subject));
        Assert.StartsWith("refused 3 times.", finding.Evidence);
        Assert.Contains("| evil.example | developer | 3 | host evil.example is not on the developer list |", report.Markdown);
        Assert.Contains("| once.example | developer | 1 |", report.Markdown);
    }

    [Fact]
    public void Without_a_refusal_log_the_report_says_it_knows_nothing_instead_of_claiming_none_were_refused()
    {
        var report = Build([Run()], deniedLog: false);

        Assert.Contains("No refusal log is available", report.Markdown);
        Assert.DoesNotContain("No sandbox was refused a host this week.", report.Markdown);
    }

    [Fact]
    public void An_empty_refusal_log_means_none_were_refused()
    {
        Assert.Contains("No sandbox was refused a host this week.", Build([Run()]).Markdown);
    }

    // ---- the week before -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(10_000, "+100% against the week before (10,000 in 1 runs)", 20_000)]
    [InlineData(40_000, "-50% against the week before (40,000 in 1 runs)", 20_000)]
    public void The_summary_compares_tokens_with_the_week_before(long before, string expected, long now)
    {
        var report = Build([Run(tokens: now)], previous: [Run(tokens: before)]);

        Assert.Contains(expected, report.Markdown);
    }

    [Fact]
    public void With_no_week_before_it_says_so()
    {
        Assert.Contains("no runs the week before", Build([Run()]).Markdown);
    }

    // ---- shape -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_findings_come_first_and_text_from_the_logs_cannot_start_a_new_section()
    {
        var report = Build([Run()], denied: [Denied("x.example", reason: "a\n## Injected heading | more"), Denied("x.example", reason: "a"), Denied("x.example", reason: "a")]);

        Assert.True(report.Markdown.IndexOf("## What repeated", StringComparison.Ordinal) < report.Markdown.IndexOf("## This week", StringComparison.Ordinal));
        Assert.DoesNotContain("\n## Injected", report.Markdown);
    }
}

public sealed class LogReadersTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "lr-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset From = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(7);

    public LogReadersTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Write(string name, params string[] lines)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string RunLine(string time, string run = "r1", string status = "pr_opened", string extra = "") =>
        $$"""{"time":"{{time}}","workflow_id":"feature-a-1","run_id":"{{run}}","kind":"feature","repository":"acme/shop","number":1,"status":"{{status}}","input":1000,"output":200,"reasoning":30,"cache_read":9999,"cache_write":40,"total":11269,"cost":0,"steps":3,"partial":false{{extra}}}""";

    [Fact]
    public void Runs_in_the_period_are_read_with_the_tokens_that_count_and_older_lines_read_as_having_no_extras()
    {
        var path = Write("u.jsonl", RunLine("2026-11-10T10:00:00Z"));

        var run = Assert.Single(LogReaders.Runs(path, From, To));

        Assert.Equal(("feature", "acme/shop", 1, "pr_opened"), (run.Kind, run.Repository, run.Number, run.Status));
        Assert.Equal(1270, run.Counted);
        Assert.Equal((0, 0), (run.Rounds, run.GateFailures));
        Assert.Empty(run.Roles);
    }

    [Fact]
    public void The_figures_a_run_added_to_the_log_are_read_back()
    {
        var path = Write("u.jsonl", RunLine("2026-11-10T10:00:00Z", extra: ""","rounds":3,"gate_failures":2,"roles":{"developer":{"attempts":4,"rejected":1,"failed":1,"tokens":900}}"""));

        var run = Assert.Single(LogReaders.Runs(path, From, To));

        Assert.Equal((3, 2), (run.Rounds, run.GateFailures));
        Assert.Equal(new RoleStats(4, 1, 1, 900), run.Roles["developer"]);
    }

    [Fact]
    public void Only_the_period_counts_and_the_end_is_not_in_it()
    {
        var path = Write("u.jsonl", RunLine("2026-11-08T23:59:59Z", "before"), RunLine("2026-11-09T00:00:00Z", "start"), RunLine("2026-11-15T23:59:59Z", "last"), RunLine("2026-11-16T00:00:00Z", "after"));

        Assert.Equal(["start", "last"], LogReaders.Runs(path, From, To).Select(run => run.RunId));
    }

    [Fact]
    public void A_run_written_twice_counts_once_and_a_line_that_cannot_be_read_is_skipped()
    {
        var path = Write("u.jsonl", RunLine("2026-11-10T10:00:00Z"), RunLine("2026-11-10T10:00:00Z"), "garbage", "{\"time\":\"2026-11-10T10:00:00Z\"}", "", RunLine("2026-11-11T10:00:00Z", "r2"));

        Assert.Equal(2, LogReaders.Runs(path, From, To).Count);
    }

    [Fact]
    public void A_missing_log_is_an_empty_one()
    {
        Assert.Empty(LogReaders.Runs(Path.Combine(folder, "none.jsonl"), From, To));
        Assert.Empty(LogReaders.Denied(Path.Combine(folder, "none.jsonl"), From, To));
        Assert.Empty(LogReaders.Denied("", From, To));
    }

    [Fact]
    public void Refusals_are_read_from_the_proxys_lines_and_only_denials_in_the_period_count()
    {
        var path = Write("d.jsonl",
            """{"time":"2026-11-10T10:00:00+00:00","profile":"developer","client":"10.250.3.5","host":"evil.example","port":443,"decision":"deny","reason":"not on the list","bytes_up":0,"bytes_down":0,"milliseconds":3}""",
            """{"time":"2026-11-10T10:00:00+00:00","profile":"developer","client":"10.250.3.5","host":"ok.example","port":443,"decision":"allow","reason":"","bytes_up":1,"bytes_down":1,"milliseconds":3}""",
            """{"time":"2026-11-20T10:00:00+00:00","profile":"developer","client":"10.250.3.5","host":"late.example","port":443,"decision":"deny","reason":"x","bytes_up":0,"bytes_down":0,"milliseconds":3}""",
            "garbage");

        var denied = Assert.Single(LogReaders.Denied(path, From, To));

        Assert.Equal(("developer", "evil.example", "not on the list"), (denied.Profile, denied.Host, denied.Reason));
    }
}
