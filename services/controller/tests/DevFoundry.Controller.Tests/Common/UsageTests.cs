namespace DevFoundry.Controller.Tests;

public sealed class OpenCodeUsageTests
{
    private const string RealStep =
        """{"type":"step_finish","timestamp":1793734444066,"part":{"reason":"stop","type":"step-finish","tokens":{"total":2903,"input":785,"output":25,"reasoning":45,"cache":{"write":0,"read":2048}},"cost":0}}""";

    [Fact]
    public void A_step_finish_event_as_opencode_writes_it_is_read()
    {
        var usage = OpenCodeEvents.Usage("""{"type":"step_start"}""" + "\n" + RealStep);

        Assert.Equal(new TokenUsage(785, 25, 45, 2048, 0, 2903, 0, 1), usage);
    }

    [Fact]
    public void Every_step_of_a_run_is_added_up()
    {
        var second = """{"type":"step_finish","part":{"cost":0.5,"tokens":{"total":100,"input":60,"output":30,"reasoning":10,"cache":{"read":0,"write":7}}}}""";

        var usage = OpenCodeEvents.Usage(RealStep + "\n" + second)!;

        Assert.Equal(2, usage.Steps);
        Assert.Equal(845, usage.Input);
        Assert.Equal(55, usage.Output);
        Assert.Equal(55, usage.Reasoning);
        Assert.Equal(2048, usage.CacheRead);
        Assert.Equal(7, usage.CacheWrite);
        Assert.Equal(3003, usage.Total);
        Assert.Equal(0.5, usage.Cost);
        Assert.False(usage.Partial);
    }

    [Fact]
    public void Text_and_tool_events_are_not_counted()
    {
        var usage = OpenCodeEvents.Usage(
            """{"type":"text","part":{"text":"hi","tokens":{"total":999}}}""" + "\n" + """{"type":"tool_use","part":{}}""" + "\n" + RealStep);

        Assert.Equal(2903, usage!.Total);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"type":"text","part":{"text":"no usage here"}}""")]
    public void A_run_without_step_events_has_no_usage(string? output) =>
        Assert.Null(OpenCodeEvents.Usage(output));

    [Fact]
    public void A_missing_total_is_worked_out_from_the_parts()
    {
        var usage = OpenCodeEvents.Usage("""{"type":"step_finish","part":{"tokens":{"input":10,"output":5,"reasoning":2,"cache":{"read":3,"write":1}}}}""");

        Assert.Equal(21, usage!.Total);
    }

    [Fact]
    public void Odd_values_count_as_zero_instead_of_breaking_the_run()
    {
        var usage = OpenCodeEvents.Usage("""{"type":"step_finish","part":{"cost":"free","tokens":{"input":"many","output":-5,"total":12}}}""");

        Assert.Equal(12, usage!.Total);
        Assert.Equal(0, usage.Input);
        Assert.Equal(0, usage.Output);
        Assert.Equal(0, usage.Cost);
    }

    [Fact]
    public void Cut_output_marks_the_numbers_as_a_lower_bound() =>
        Assert.True(OpenCodeEvents.Usage(RealStep, truncated: true)!.Partial);
}

public sealed class AgentCatalogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ac-" + Guid.NewGuid().ToString("N"));

    public AgentCatalogTests() => Directory.CreateDirectory($"{directory}/agents");

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private void Role(string name, string content) => File.WriteAllText($"{directory}/agents/{name}.md", content);

    [Fact]
    public void The_model_comes_from_the_role_file()
    {
        Role("developer", "---\ndescription: x\nmode: primary\nmodel: openai/gpt-6.1-sol\npermission:\n  edit: allow\n---\nPrompt text\n");

        Assert.Equal("openai/gpt-6.1-sol", AgentCatalog.ModelFor(directory, "developer"));
    }

    [Fact]
    public void A_role_without_its_own_model_gets_the_default_from_opencode_json()
    {
        Role("qa", "---\ndescription: x\n---\nPrompt\n");
        File.WriteAllText($"{directory}/opencode.json", """{ "model": "openai/default-model", }""");

        Assert.Equal("openai/default-model", AgentCatalog.ModelFor(directory, "qa"));
    }

    [Fact]
    public void A_model_line_in_the_prompt_text_is_not_mistaken_for_configuration()
    {
        Role("analyst", "---\ndescription: x\n---\nmodel: not-this-one\n");

        Assert.Null(AgentCatalog.ModelFor(directory, "analyst"));
    }

    [Theory]
    [InlineData("../agents/developer")]
    [InlineData("Developer")]
    [InlineData("")]
    [InlineData("a/b")]
    public void A_role_name_that_could_leave_the_folder_finds_nothing(string role)
    {
        Role("developer", "---\nmodel: openai/x\n---\n");

        Assert.Null(AgentCatalog.ModelFor(directory, role));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/nonexistent/folder")]
    public void Without_an_agent_folder_the_model_is_simply_unknown(string? folder) =>
        Assert.Null(AgentCatalog.ModelFor(folder, "developer"));

    [Fact]
    public void A_broken_config_file_does_not_throw()
    {
        File.WriteAllText($"{directory}/opencode.json", "{ not json");

        Assert.Null(AgentCatalog.ModelFor(directory, "developer"));
    }
}

public sealed class UsageRecordTests
{
    private static RecordEntry Step(int sequence, string slug, TokenUsage? usage, string kind = "agent", string? model = "openai/gpt-6.1-sol") =>
        new("feature-acme-shop-1-abcd1234", sequence, kind, slug, slug, 1, "2026-11-03 10:00:00Z", 2.5, "accepted", 0, false, "in", "out",
            Model: kind == "agent" ? model : null, Usage: usage);

    private static TokenUsage Used(long total, double cost = 0) => new(total / 2, total / 4, 0, total / 4, 0, total, cost, 1);

    private static readonly RunRecordSpec Spec = new(".factory/runs/issue-1-abcd1234", "h", "o");

    [Fact]
    public void A_step_file_names_the_model_tokens_and_cost()
    {
        var text = RunRecord.Render(Step(1, "developer", new TokenUsage(785, 25, 45, 2048, 0, 2903, 0.0123, 2)), []);

        Assert.Contains("- model: openai/gpt-6.1-sol (as configured for this role)", text);
        Assert.Contains("- tokens: 2903 in total (input 785, output 25, reasoning 45, cache read 2048, cache write 0)", text);
        Assert.Contains("- cost: 0.0123 (as reported by OpenCode)", text);
    }

    [Fact]
    public void A_check_has_no_usage_lines()
    {
        var text = RunRecord.Render(Step(1, "gate-test", null, kind: "gate"), []);

        Assert.DoesNotContain("- tokens:", text);
        Assert.DoesNotContain("- model:", text);
    }

    [Fact]
    public void The_summary_adds_up_a_role_over_all_its_attempts_and_shows_the_total()
    {
        var entries = new[]
        {
            Step(1, "developer", Used(1000, 0.5)),
            Step(2, "gate-test", null, kind: "gate"),
            Step(3, "developer", Used(400, 0.25)),
            Step(4, "reviewer", Used(600)),
        };

        var summary = RunRecord.Summary(Spec, entries);

        Assert.Contains("## Usage", summary);
        Assert.Contains("| developer | openai/gpt-6.1-sol | 2 | 700 | 350 | 0 | 350 | 1400 | 0.7500 |", summary);
        Assert.Contains("| reviewer | openai/gpt-6.1-sol | 1 |", summary);
        Assert.Contains("| **all** | | 3 | 1000 | 500 | 0 | 500 | **2000** | 0.7500 |", summary);
        Assert.DoesNotContain("| gate-test | openai", summary);
        Assert.DoesNotContain("lower bound", summary);
    }

    [Fact]
    public void A_run_with_no_usage_has_no_usage_section_and_no_usage_file()
    {
        var entries = new[] { Step(1, "gate-test", null, kind: "gate") };

        Assert.DoesNotContain("## Usage", RunRecord.Summary(Spec, entries));
        Assert.False(RunRecord.HasUsage(entries));
    }

    [Fact]
    public void Cut_output_is_flagged_in_the_summary() =>
        Assert.Contains("lower bound", RunRecord.Summary(Spec, [Step(1, "developer", Used(100) with { Partial = true })]));

    [Fact]
    public void The_usage_file_has_the_total_by_role_and_every_step()
    {
        var entries = new[] { Step(1, "developer", Used(1000, 0.5)), Step(2, "developer", Used(400, 0.25)), Step(3, "qa", Used(600)) };

        using var json = System.Text.Json.JsonDocument.Parse(RunRecord.UsageJson(entries));
        var root = json.RootElement;

        Assert.Equal(2000, root.GetProperty("total").GetProperty("total").GetInt64());
        Assert.Equal(0.75, root.GetProperty("total").GetProperty("cost").GetDouble());
        var developer = root.GetProperty("by_role").GetProperty("developer");
        Assert.Equal(2, developer.GetProperty("runs").GetInt32());
        Assert.Equal("openai/gpt-6.1-sol", developer.GetProperty("model").GetString());
        Assert.Equal(1400, developer.GetProperty("usage").GetProperty("total").GetInt64());
        Assert.Equal(3, root.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public void The_index_lists_model_and_tokens_for_each_step()
    {
        var index = RunRecord.Index([Step(1, "developer", Used(1000, 0.5))]);

        Assert.Contains("\"model\": \"openai/gpt-6.1-sol\"", index);
        Assert.Contains("\"tokens\": 1000", index);
    }
}
