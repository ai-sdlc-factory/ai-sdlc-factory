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

    private static ImprovementReports BuildAll(IReadOnlyList<LoggedRun> runs, IReadOnlyList<LoggedRun>? previous = null, IReadOnlyList<PullOutcome>? pulls = null) =>
        ImprovementReportBuilder.BuildAll("2026-W46", From, To, runs, previous ?? [], [], true, pulls);

    private static IEnumerable<LoggedRun> Runs(int count, string repository, string status = "pr_opened", long tokens = 10_000, int firstNumber = 1) =>
        Enumerable.Range(firstNumber, count).Select(number => Run(status: status, repository: repository, number: number, tokens: tokens));

    private static string[] FindingAreas(ImprovementReport report) => report.Findings.Select(finding => finding.Area).ToArray();

    // ---- one report for each repository -----------------------------------------------------------------------

    [Fact]
    public void Each_repository_gets_a_report_of_its_own_made_from_its_own_runs()
    {
        var all = BuildAll([.. Runs(6, "acme/shop"), .. Runs(2, "acme/api", firstNumber: 20)]);

        Assert.Equal(["acme/shop", "acme/api"], all.Repositories.Select(r => r.Repository)); // the one that used the most tokens first
        var shop = all.Repositories[0].Report.Markdown;
        Assert.Contains("# Factory report, 2026-W46, acme/shop (2026-11-09 to 2026-11-16)", shop);
        Assert.Contains("covers **acme/shop** only", shop);
        Assert.Contains("**6 runs**", shop);
        Assert.DoesNotContain("acme/api", shop);
        Assert.DoesNotContain("Hosts a sandbox was refused", shop); // the proxy does not say which repository a refusal was for
        Assert.DoesNotContain("- In ", shop); // one repository: no list of repositories
    }

    [Fact]
    public void The_full_report_lists_the_repositories_and_where_their_reports_are_and_the_ticket_text_does_not_have_the_list()
    {
        LoggedRun[] runs = [.. Runs(6, "acme/shop"), .. Runs(2, "acme/api", firstNumber: 20)];
        var all = BuildAll(runs);

        Assert.Contains("## By repository", all.Whole.Markdown);
        Assert.Contains("| acme/shop | 6 | 60,000 | 6 of 6 | 0 | `2026-W46/acme_shop.md` |", all.Whole.Markdown);
        Assert.Contains("| acme/api | 2 | 20,000 | 2 of 2 | too few runs | `2026-W46/acme_api.md` |", all.Whole.Markdown);
        Assert.DoesNotContain("## By repository", all.TicketMarkdown);
        // The text that is posted and read by the improver is exactly what the report was before the list existed.
        Assert.Equal(Build(runs).Markdown, all.TicketMarkdown);
        Assert.Equal(FindingAreas(Build(runs)), FindingAreas(all.Whole));
    }

    [Fact]
    public void A_repository_with_few_runs_is_shown_but_never_flagged_while_the_full_report_still_flags_the_pattern()
    {
        var all = BuildAll(Runs(3, "acme/small", status: "build_failed").ToList());

        Assert.True(all.Whole.HasFindings); // three runs that ended the same way
        var small = Assert.Single(all.Repositories).Report;
        Assert.False(small.HasFindings);
        Assert.Contains("Only 3 runs finished in this repository this week (fewer than 5), so nothing is flagged.", small.Markdown);
        Assert.DoesNotContain("Nothing repeated enough", small.Markdown);
        Assert.Contains("| acme/small | 3 | 30,000 | 0 of 3 | too few runs |", all.Whole.Markdown);
    }

    [Fact]
    public void A_repository_with_enough_runs_is_flagged_by_the_same_rules_as_the_whole_factory()
    {
        var all = BuildAll([.. Runs(5, "acme/shop", status: "build_failed"), .. Runs(6, "acme/api", firstNumber: 20)]);

        var shop = all.Repositories.Single(r => r.Repository == "acme/shop").Report;
        var api = all.Repositories.Single(r => r.Repository == "acme/api").Report;
        Assert.Equal(["Runs that stopped the same way"], FindingAreas(shop));
        Assert.Contains("5 runs, in 1 repository.", shop.Markdown);
        Assert.Empty(api.Findings);
        Assert.Contains("| acme/shop | 5 | 50,000 | 0 of 5 | 1 |", all.Whole.Markdown);
        Assert.Contains("| acme/api | 6 | 60,000 | 6 of 6 | 0 |", all.Whole.Markdown);
    }

    [Fact]
    public void Pull_requests_and_the_week_before_are_taken_from_that_repository_only()
    {
        var pulls = new[]
        {
            new PullOutcome("acme/shop", 1, "feature", PullStates.Merged, 0, 1.0),
            new PullOutcome("acme/api", 21, "feature", PullStates.Merged, 1, 2.0),
            new PullOutcome("acme/api", 22, "bug", PullStates.Open, 0, null),
        };
        var all = BuildAll([.. Runs(2, "acme/shop"), .. Runs(2, "acme/api", firstNumber: 20)], previous: [.. Runs(4, "acme/shop", firstNumber: 50)], pulls: pulls);

        var shop = all.Repositories.Single(r => r.Repository == "acme/shop").Report.Markdown;
        var api = all.Repositories.Single(r => r.Repository == "acme/api").Report.Markdown;
        Assert.Contains("| **all** | 1 | 1 |", shop);
        Assert.Contains("| **all** | 2 | 1 |", api);
        Assert.Contains("-50% against the week before", shop); // 20,000 now against 40,000 before
        Assert.Contains("no runs the week before", api);
    }

    [Fact]
    public void When_the_git_host_could_not_be_asked_each_repository_says_so()
    {
        var all = BuildAll(Runs(2, "acme/shop").ToList(), pulls: null);

        Assert.Contains("The git host could not be asked", Assert.Single(all.Repositories).Report.Markdown);
    }

    [Fact]
    public void File_names_are_safe_and_unique_even_when_two_repositories_would_get_the_same_one()
    {
        var all = BuildAll([.. Runs(1, "github:acme/shop"), .. Runs(1, "github_acme/shop", firstNumber: 5), .. Runs(1, "../../etc/passwd", firstNumber: 9), .. Runs(1, "...", firstNumber: 12)]);

        var names = all.Repositories.Select(r => r.FileName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, name => Assert.Matches(@"\A[A-Za-z0-9_][A-Za-z0-9._-]*\.md\z", name));
        Assert.Contains("github_acme_shop.md", names);
        Assert.Contains("github_acme_shop-2.md", names);
    }

    [Fact]
    public void Only_the_repositories_that_used_the_most_tokens_get_a_report_and_the_rest_are_counted()
    {
        var runs = Enumerable.Range(1, ReportRules.MaxRepositories + 2).Select(index => Run(repository: $"acme/r{index:00}", number: index, tokens: 1_000 * index)).ToList();

        var all = BuildAll(runs);

        Assert.Equal(ReportRules.MaxRepositories, all.Repositories.Count);
        Assert.DoesNotContain(all.Repositories, r => r.Repository is "acme/r01" or "acme/r02"); // the two that used the fewest
        Assert.Contains("2 more repositories used fewer tokens and have no report of their own (at most 50 get one).", all.Whole.Markdown);
    }

    [Fact]
    public void A_week_with_no_runs_has_no_repositories_and_no_list()
    {
        var all = BuildAll([]);

        Assert.Empty(all.Repositories);
        Assert.DoesNotContain("## By repository", all.Whole.Markdown);
        Assert.Equal(all.Whole.Markdown, all.TicketMarkdown);
    }

    [Fact]
    public void One_run_is_not_called_runs()
    {
        Assert.Contains("- **1 run** (feature 1) used **10,000 tokens**", Build([Run()]).Markdown);
    }

    [Fact]
    public void One_repository_is_not_called_repositories()
    {
        var report = Build([.. Runs(1, "acme/shop"), .. Runs(3, "acme/shop", status: "build_failed", firstNumber: 5)]);

        Assert.Contains("- In 1 repository: acme/shop 4.", report.Markdown);
    }

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
    public void An_answered_question_is_a_healthy_ending_even_when_it_happens_often()
    {
        var report = Build([
            Run(kind: "ask", status: "answered"), Run(kind: "ask", status: "answered", number: 2), Run(kind: "ask", status: "answered", number: 3),
            Run(kind: "ask", status: "answered", number: 4, repository: "acme/api")]);

        Assert.False(report.HasFindings);
        Assert.DoesNotContain("| ask | answered |", report.Markdown);
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
