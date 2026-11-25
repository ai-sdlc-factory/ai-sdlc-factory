using System.Text.RegularExpressions;

namespace AiSdlcFactory.Controller;

/// <summary>
/// Whether the run of a bug's reproduction test stopped before any test could say anything. A non-zero exit code is not enough to call a test "failing": a build error, a collection
/// error or an import error also exits non-zero, and goes away as soon as the missing code exists, whatever that code does, so it would "prove" a bug that was never shown.
/// Only what is known for certain is refused (the exit codes pytest keeps for "no test ran to a result", and the messages the common tools print when they could not build or load
/// the tests); anything else non-zero is still taken as a failing test, as before.
/// </summary>
public static partial class RedCheck
{
    /// <summary>Why the run did not get to a failing test, or null when it did (or there is no sign that it did not).</summary>
    public static string? DidNotRunTests(IReadOnlyList<string> command, long exitCode, string output)
    {
        if (exitCode != 0 && exitCode != 1 && UsesPytest(command))
        {
            return exitCode switch
            {
                2 => "pytest exited with code 2: the tests were interrupted or could not be collected (an import error or a syntax error in a test file)",
                3 => "pytest exited with code 3: an internal error",
                4 => "pytest exited with code 4: the command line was wrong",
                5 => "pytest exited with code 5: it found no tests to run (the test was not collected, so the name or the path is wrong)",
                _ => $"pytest exited with code {exitCode}, which is not the code for a failing test (1)",
            };
        }
        foreach (var (pattern, reason) in Signs)
        {
            if (pattern().IsMatch(output))
            {
                return reason;
            }
        }
        return null;
    }

    private static bool UsesPytest(IReadOnlyList<string> command) =>
        command.Any(word => word is "pytest" or "py.test" || word.EndsWith("/pytest", StringComparison.Ordinal));

    private static readonly (Func<Regex> Pattern, string Reason)[] Signs =
    [
        (Build, "the code or the test did not build (a compiler error)"),
        (Collection, "a test file could not be loaded (a collection or import error)"),
        (NotRun, "the test runner reported that the test suite failed to run"),
    ];

    [GeneratedRegex(@"(?m)\berror (?:CS|FS|VB|MSB|NETSDK)\d{3,4}\b|^\s*Build FAILED\b|\[(?:build|setup) failed\]|\bcould not compile\b|\bCOMPILATION ERROR\b|\berror TS\d{4}:")]
    private static partial Regex Build();

    [GeneratedRegex(@"(?m)\bERROR collecting\b|\berrors? during collection\b|\bImportError while importing test module\b")]
    private static partial Regex Collection();

    [GeneratedRegex(@"\bTest suite failed to run\b|\bFailed to resolve import\b")]
    private static partial Regex NotRun();
}
