using Xunit;

namespace AiSdlcFactory.Controller.Tests;

public sealed class TesterReportTests
{
    private static string Json(string verdict = "tests_written", string summary = "Tried it.", string scenarios = """[{"name":"goodbye","suite":"api","covers":[1,2]}]""",
        string files = """["e2e/api/a.test"]""", string findings = "[]", string assessments = "[]") =>
        $$"""{"verdict":"{{verdict}}","summary":"{{summary}}","scenarios":{{scenarios}},"files":{{files}},"findings":{{findings}},"assessments":{{assessments}}}""";

    private static string Error(string json)
    {
        Assert.False(TesterParser.TryParse(json, out _, out var error));
        return error;
    }

    [Fact]
    public void A_complete_answer_is_read()
    {
        const string findings = """[{"severity":"major","message":"Empty id gives 500.","steps":"GET /api/products?id="}]""";
        const string assessments = """[{"test":"goodbye","verdict":"application_bug","explanation":"Returns Hello."}]""";
        Assert.True(TesterParser.TryParse(Json(findings: findings, assessments: assessments), out var report, out var error), error);

        Assert.Equal(TesterReport.Written, report!.Verdict);
        Assert.Equal(("goodbye", "api"), (report.Scenarios[0].Name, report.Scenarios[0].Suite));
        Assert.Equal([1, 2], report.Scenarios[0].Covers);
        Assert.Equal(["e2e/api/a.test"], report.Files);
        Assert.Equal("major", report.Findings[0].Severity);
        Assert.True(report.BlamesOnlyTheApplication);
    }

    [Fact]
    public void An_answer_after_other_text_is_found()
    {
        Assert.True(TesterParser.TryParse("Done.\n```json\n" + Json() + "\n```", out var report, out _));
        Assert.Equal(TesterReport.Written, report!.Verdict);
    }

    [Fact]
    public void Blocked_needs_only_a_summary()
    {
        Assert.True(TesterParser.TryParse("""{"verdict":"blocked","summary":"The app does not start."}""", out var report, out var error), error);
        Assert.Equal(TesterReport.Blocked, report!.Verdict);
        Assert.Empty(report.Scenarios);
    }

    [Fact]
    public void A_test_problem_does_not_make_the_application_the_only_one_to_blame()
    {
        const string mixed = """[{"test":"a","verdict":"application_bug","explanation":"x"},{"test":"b","verdict":"test_problem","explanation":"y"}]""";
        Assert.True(TesterParser.TryParse(Json(assessments: mixed), out var report, out _));
        Assert.False(report!.BlamesOnlyTheApplication);
        Assert.True(TesterParser.TryParse(Json(), out var none, out _));
        Assert.False(none!.BlamesOnlyTheApplication); // nothing assessed
    }

    [Theory]
    [InlineData("""{"verdict":"maybe","summary":"x"}""", "tests_written, blocked")]
    [InlineData("""{"verdict":"tests_written","summary":""}""", "\"summary\"")]
    [InlineData("""{"verdict":"tests_written","summary":"x"}""", "\"scenarios\"")]
    public void An_answer_that_is_incomplete_says_what_is_missing(string json, string expected)
    {
        Assert.Contains(expected, Error(json));
    }

    [Fact]
    public void Every_limit_is_checked_and_named()
    {
        Assert.Contains("at least one scenario", Error(Json(scenarios: "[]")));
        Assert.Contains("\"suite\" of api or ui", Error(Json(scenarios: """[{"name":"x","suite":"smoke","covers":[]}]""")));
        Assert.Contains("\"covers\"", Error(Json(scenarios: """[{"name":"x","suite":"api","covers":["one"]}]""")));
        Assert.Contains("\"covers\"", Error(Json(scenarios: """[{"name":"x","suite":"api","covers":[0]}]""")));
        Assert.Contains("\"files\" must be plain relative paths", Error(Json(files: """["../outside"]""")));
        Assert.Contains("\"files\" must be plain relative paths", Error(Json(files: """["/etc/passwd"]""")));
        Assert.Contains("\"findings\" item 1", Error(Json(findings: """[{"severity":"huge","message":"m","steps":"s"}]""")));
        Assert.Contains("\"findings\" item 1", Error(Json(findings: """[{"severity":"major","message":"m"}]""")));
        Assert.Contains("\"assessments\" item 1", Error(Json(assessments: """[{"test":"t","verdict":"maybe","explanation":"e"}]""")));
        Assert.Contains("at most 2000", Error(Json(summary: new string('x', 2001))));
        var tooMany = "[" + string.Join(",", Enumerable.Range(0, 31).Select(i => $$"""{"name":"s{{i}}","suite":"api","covers":[]}""")) + "]";
        Assert.Contains("at most 30", Error(Json(scenarios: tooMany)));
    }
}
