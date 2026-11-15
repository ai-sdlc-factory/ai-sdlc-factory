using Microsoft.Extensions.Configuration;

namespace DevFoundry.Controller.Tests;

public sealed class ProviderLimitTests
{
    [Theory]
    [InlineData(134, "SANDBOX_PIDS_LIMIT")]
    [InlineData(137, "SANDBOX_MEMORY_BYTES")]
    [InlineData(1, "")]
    public void An_exit_code_that_means_a_sandbox_limit_is_explained(int code, string mentions)
    {
        var hint = FactoryWorkflowBase.ExitHint(code);

        Assert.Equal(mentions.Length == 0, hint.Length == 0);
        Assert.Contains(mentions, hint);
    }

    [Theory]
    [InlineData("Error: You have hit your usage limit. Try again in 3 hours.")]
    [InlineData("You've reached your plan's usage limit for GPT.")]
    [InlineData("429 Too Many Requests")]
    [InlineData("HTTP 429: rate limit exceeded")]
    [InlineData("rate_limit_exceeded")]
    [InlineData("insufficient_quota: You exceeded your current quota, please check your plan")]
    [InlineData("Quota exceeded for this account")]
    [InlineData("request failed with status code 429")]
    public void The_providers_wording_on_standard_error_is_recognised(string text)
    {
        var said = ProviderLimit.Detect("", text);

        Assert.NotNull(said);
        Assert.DoesNotContain('\n', said);
    }

    [Fact]
    public void What_OpenCode_1_19_really_prints_for_a_429_is_recognised()
    {
        // Captured from the real opencode run --format json against a provider that answers 429 insufficient_quota (2026-11-15).
        var output = """
{"type":"error","timestamp":1794744929352,"sessionID":"ses_e20deb9d4ffeq9o0PvWGNPjdxK","error":{"name":"APIError","data":{"message":"You exceeded your current quota, please check your plan and billing details.","statusCode":429,"isRetryable":true,"responseHeaders":{"content-length":"162","content-type":"application/json","date":"Sun, 15 Nov 2026 12:15:29 GMT","server":"BaseHTTP/0.6 Python/3.12.3"},"responseBody":"{\"error\": {\"message\": \"You exceeded your current quota, please check your plan and billing details.\", \"type\": \"insufficient_quota\", \"code\": \"insufficient_quota\"}}","metadata":{"url":"http://host.docker.internal:9429/v1/responses"}}}}
""";

        var said = ProviderLimit.Detect(output, "");

        Assert.NotNull(said);
        Assert.Contains("quota", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_error_event_in_the_output_is_recognised_wherever_the_message_sits_in_it()
    {
        var output = """{"type":"step_start"}""" + "\n" + """{"type":"error","error":{"name":"APIError","data":{"statusCode":429,"message":"Usage limit reached"}}}""";

        var said = ProviderLimit.Detect(output, "");

        Assert.Contains("429", said);
    }

    [Theory]
    [InlineData("connection refused")]
    [InlineData("OpenCode exited with code 1")]
    [InlineData("Error: model not found")]
    [InlineData("TypeError: cannot read properties of undefined")]
    [InlineData("")]
    public void Other_failures_are_not_mistaken_for_a_limit(string text) => Assert.Null(ProviderLimit.Detect("", text));

    [Fact]
    public void What_the_agent_itself_wrote_or_read_is_never_looked_at()
    {
        // A repository may well talk about rate limits. Only error events and standard error say how a run failed.
        var output = string.Join("\n",
            """{"type":"text","part":{"text":"I added a rate limit of 100 requests, and the usage limit is documented. HTTP 429 is returned."}}""",
            """{"type":"tool_use","part":{"output":"if status == 429: wait for the rate limit"}}""");

        Assert.Null(ProviderLimit.Detect(output, ""));
    }

    [Fact]
    public void A_line_cut_off_by_truncation_or_not_json_does_not_break_the_search()
    {
        var output = """{"type":"error","error":{"message":"usage li""" + "\n" + "not json at all" + "\n" + """{"type":"error","message":"rate limit"}""";

        Assert.Contains("rate limit", ProviderLimit.Detect(output, ""));
    }

    [Fact]
    public void The_quote_is_short_even_when_the_message_is_long()
    {
        var said = ProviderLimit.Detect("", new string('x', 5000) + " usage limit " + new string('y', 5000));

        Assert.True(said!.Length < 300, said.Length.ToString());
    }
}

public sealed class TokenLimitsTests
{
    [Fact]
    public void What_counts_is_input_output_reasoning_and_cache_writes_but_not_cache_reads()
    {
        var usage = new TokenUsage(Input: 1000, Output: 200, Reasoning: 30, CacheRead: 5000, CacheWrite: 40, Total: 6270, Cost: 0, Steps: 3);

        Assert.Equal(1270, usage.Counted);
    }

    [Theory]
    [InlineData(null, 0, 0)]
    [InlineData(null, 500_000, 500_000)]
    [InlineData(50_000, 0, 50_000)]
    [InlineData(50_000, 500_000, 50_000)]
    [InlineData(900_000, 500_000, 500_000)]
    public void A_project_can_only_lower_the_operators_maximum(int? project, long maximum, long expected) =>
        Assert.Equal(expected, ProjectLimits.EffectiveTokenBudget(project, maximum));

    [Fact]
    public void The_budget_is_part_of_a_projects_resolved_limits()
    {
        var limits = new ProjectLimits(TokenBudget: 80_000);

        Assert.Equal(60_000, limits.Resolve(FactoryDefaults.Standard with { MaxTokensPerRun = 60_000 }).TokenBudget);
        Assert.Equal(80_000, limits.Resolve(FactoryDefaults.Standard).TokenBudget);
        Assert.Equal(0, new ProjectLimits().Resolve(FactoryDefaults.Standard).TokenBudget);
    }

    private static string Json(string limits) => $$"""{"image":"python","gates":[{"name":"test","command":["pytest"]}],"limits":{{limits}}}""";

    [Fact]
    public void A_budget_in_the_project_file_is_read()
    {
        Assert.True(ProjectConfigParser.TryParse(Json("""{"token_budget":250000,"build_rounds":2}"""), out var config, out var error), error);

        Assert.Equal(250_000, config.Limits!.TokenBudget);
        Assert.Equal(2, config.Limits.BuildRounds);
    }

    [Theory]
    [InlineData("9999")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1000000001")]
    [InlineData("\"50000\"")]
    [InlineData("50000.5")]
    public void A_budget_outside_its_range_is_refused_with_the_range(string value)
    {
        Assert.False(ProjectConfigParser.TryParse(Json($$"""{"token_budget":{{value}}}"""), out _, out var error));

        Assert.Contains("limits.token_budget", error);
        Assert.Contains("10,000", error);
    }

    [Fact]
    public void The_message_for_a_misspelled_limit_lists_the_budget_too()
    {
        Assert.False(ProjectConfigParser.TryParse(Json("""{"token_budgets":1}"""), out _, out var error));

        Assert.Contains("token_budget", error);
    }

    private static WorkerSettings Settings(params (string Key, string Value)[] values) =>
        WorkerSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

    [Fact]
    public void The_operators_token_limits_are_off_unless_set()
    {
        var defaults = Settings().Defaults;

        Assert.Equal((0L, 0L, 0L), (defaults.MaxTokensPerRun, defaults.DailyTokenLimit, defaults.RepoDailyTokenLimit));
        Assert.Equal(0, Settings(("FACTORY_MAX_TOKENS_PER_RUN", "  ")).Defaults.MaxTokensPerRun);
        Assert.Equal(0, Settings(("FACTORY_DAILY_TOKEN_LIMIT", "0")).Defaults.DailyTokenLimit);
    }

    [Fact]
    public void The_operators_token_limits_are_read()
    {
        var defaults = Settings(("FACTORY_MAX_TOKENS_PER_RUN", "400000"), ("FACTORY_DAILY_TOKEN_LIMIT", "5000000"), ("FACTORY_REPO_DAILY_TOKEN_LIMIT", "2000000")).Defaults;

        Assert.Equal((400_000L, 5_000_000L, 2_000_000L), (defaults.MaxTokensPerRun, defaults.DailyTokenLimit, defaults.RepoDailyTokenLimit));
    }

    [Theory]
    [InlineData("FACTORY_MAX_TOKENS_PER_RUN", "lots")]
    [InlineData("FACTORY_MAX_TOKENS_PER_RUN", "5000")]
    [InlineData("FACTORY_DAILY_TOKEN_LIMIT", "-1")]
    [InlineData("FACTORY_REPO_DAILY_TOKEN_LIMIT", "1.5")]
    [InlineData("FACTORY_DAILY_TOKEN_LIMIT", "5,000,000")]
    public void A_token_limit_that_is_not_a_plain_number_stops_the_start_instead_of_switching_the_limit_off(string key, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Settings((key, value)));

        Assert.Contains(key, error.Message);
        Assert.Contains("10,000", error.Message);
    }
}

public sealed class UsageTodayTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ut-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);

    public UsageTodayTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private GitWorkspace Workspace() => new(new WorkerSettings { WorkspaceRoot = root });

    private static UsageLogEntry Entry(string run, string repository, long input = 1000) =>
        new($"feature-{run}", run, "feature", repository, 1, "pr_opened", new TokenUsage(input, 200, 30, 9_999, 40, 12_000, 0, 3));

    [Fact]
    public async Task Only_todays_runs_count_in_all_and_in_the_repository_and_cache_reads_do_not()
    {
        var workspace = Workspace();
        await workspace.LogUsageAsync(Entry("a", "acme/shop"), Now.AddHours(-3));
        await workspace.LogUsageAsync(Entry("b", "acme/api", 2000), Now.AddHours(-1));
        await workspace.LogUsageAsync(Entry("old", "acme/shop"), Now.AddDays(-1));
        await workspace.LogUsageAsync(Entry("tomorrow", "acme/shop"), Now.AddDays(1));

        var today = workspace.UsageToday("acme/shop", Now);

        Assert.Equal(1000 + 200 + 30 + 40, today.Repository);
        Assert.Equal(1270 + 2270, today.All);
    }

    [Fact]
    public async Task A_day_is_a_utc_day()
    {
        var workspace = Workspace();
        await workspace.LogUsageAsync(Entry("late", "acme/shop"), new DateTimeOffset(2026, 11, 14, 23, 59, 59, TimeSpan.Zero));
        await workspace.LogUsageAsync(Entry("early", "acme/shop"), new DateTimeOffset(2026, 11, 15, 0, 0, 1, TimeSpan.Zero));
        // 01:30 at +02:00 is 23:30 UTC the day before.
        await workspace.LogUsageAsync(Entry("offset", "acme/shop"), new DateTimeOffset(2026, 11, 14, 1, 30, 0, TimeSpan.FromHours(2)));

        Assert.Equal(1270, workspace.UsageToday("acme/shop", new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)).All);
        Assert.Equal(1270, workspace.UsageToday("acme/shop", new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero)).All);
    }

    [Fact]
    public async Task A_run_written_twice_counts_once()
    {
        var workspace = Workspace();
        await workspace.LogUsageAsync(Entry("same", "acme/shop"), Now);
        await workspace.LogUsageAsync(Entry("same", "acme/shop"), Now);

        Assert.Equal(1270, workspace.UsageToday("acme/shop", Now).All);
    }

    [Fact]
    public void No_log_means_nothing_has_been_used()
    {
        Assert.Equal(new UsageToday(0, 0), Workspace().UsageToday("acme/shop", Now));
    }

    [Fact]
    public async Task A_line_that_is_cut_short_or_not_ours_is_skipped_and_the_rest_still_counts()
    {
        var workspace = Workspace();
        await workspace.LogUsageAsync(Entry("a", "acme/shop"), Now);
        File.AppendAllText(GitWorkspace.UsageLogPath(root), "{\"time\":\"2026-11-14T15:00:00Z\",\"run_id\":\"cut\",\"input\":\n" + "{\"time\":\"2026-11-14T15:00:00Z\"}\n" + "garbage 2026-11-14\n");
        await workspace.LogUsageAsync(Entry("b", "acme/shop"), Now);

        Assert.Equal(2540, workspace.UsageToday("acme/shop", Now).All);
    }

    [Fact]
    public async Task Another_repository_with_a_similar_name_is_not_this_one()
    {
        var workspace = Workspace();
        await workspace.LogUsageAsync(Entry("a", "acme/shop-2"), Now);

        Assert.Equal(0, workspace.UsageToday("acme/shop", Now).Repository);
        Assert.Equal(1270, workspace.UsageToday("acme/shop", Now).All);
    }
}
