namespace AiSdlcFactory.Controller.Tests;

/// <summary>
/// The check that a "failing" reproduction test really ran and failed: a build, collection or import error also exits non-zero and must not count, and a genuine assertion
/// failure from any tool must still count.
/// </summary>
public sealed class RedCheckTests
{
    private static readonly string[] Pytest = ["python", "-m", "pytest", "-q", "test_bug.py"];
    private static readonly string[] DotnetTest = ["dotnet", "test", "--filter", "Bug"];

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Pytest_exit_codes_other_than_1_mean_no_test_failed(int code)
    {
        Assert.NotNull(RedCheck.DidNotRunTests(Pytest, code, "anything"));
    }

    [Fact]
    public void Pytest_exit_1_with_an_assertion_is_a_failing_test()
    {
        Assert.Null(RedCheck.DidNotRunTests(Pytest, 1, "E assert 'abab' == 'ababab'\n1 failed in 0.02s"));
    }

    [Fact]
    public void Pytest_found_by_its_path_is_still_pytest()
    {
        Assert.NotNull(RedCheck.DidNotRunTests([".venv/bin/pytest", "-q"], 2, ""));
    }

    [Fact]
    public void A_command_that_is_not_pytest_keeps_its_exit_code_unjudged()
    {
        Assert.Null(RedCheck.DidNotRunTests(["go", "test", "./..."], 2, "--- FAIL: TestBug (0.00s)\n    got 1, want 2"));
    }

    [Theory]
    [InlineData("Build FAILED.\n    Program.cs(5,9): error CS0103: The name 'Missing' does not exist in the current context")]
    [InlineData("src/Bug.cs(12,3): error CS1002: ; expected")]
    [InlineData("error MSB1009: Project file does not exist.")]
    [InlineData("FAIL example.com/pkg [build failed]")]
    [InlineData("error: could not compile `shop` due to 2 previous errors")]
    [InlineData("[ERROR] COMPILATION ERROR : ")]
    [InlineData("ERROR collecting test_bug.py\nImportError while importing test module")]
    [InlineData("Interrupted: 1 error during collection")]
    [InlineData("FAIL  src/bug.test.ts\n  Test suite failed to run\n    error TS2304: Cannot find name 'missing'.")]
    [InlineData("Error: Failed to resolve import \"./missing\" from \"src/bug.test.ts\"")]
    public void A_build_collection_or_import_error_is_not_a_failing_test(string output)
    {
        Assert.NotNull(RedCheck.DidNotRunTests(DotnetTest, 1, output));
    }

    [Theory]
    [InlineData("Failed Bug_is_fixed [4 ms]\n  Assert.Equal() Failure: Expected: 3  Actual: 2\nFailed!  - Failed: 1, Passed: 0")]
    [InlineData("--- FAIL: TestBug (0.00s)\n    bug_test.go:12: got 1, want 2\nFAIL")]
    [InlineData("● bug › returns the sum\n  expect(received).toBe(expected)\n  Expected: 3\n  Received: 2")]
    [InlineData("")]
    public void A_genuine_assertion_failure_is_a_failing_test(string output)
    {
        Assert.Null(RedCheck.DidNotRunTests(DotnetTest, 1, output));
    }

    [Fact]
    public void The_reason_names_what_happened_so_the_reproducer_can_act_on_it()
    {
        var reason = RedCheck.DidNotRunTests(Pytest, 5, "no tests ran");

        Assert.Contains("no tests", reason);
    }
}
