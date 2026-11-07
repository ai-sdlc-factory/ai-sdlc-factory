using System.Text;

namespace DevFoundry.Controller;

/// <summary>
/// The commands a QA run gives the sandbox. The application and the tests must share one sandbox (so that "localhost" is the same for both), which
/// takes a small shell script: start the application in the background, wait until it answers, run the suite, stop the application. Everything that
/// comes from the repository's <c>project.json</c> is single-quoted into the script, never spliced in as shell syntax.
/// </summary>
public static class VerifyScript
{
    /// <summary>The script's own exit code when the application stopped before it answered.</summary>
    public const int AppExitedEarly = 97;

    /// <summary>The script's own exit code when the application did not answer within the startup time.</summary>
    public const int AppNotReady = 98;

    /// <summary>Quotes a value for a POSIX shell: inside single quotes nothing is special, and a single quote is closed, escaped and reopened.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string Quote(IEnumerable<string> command) => string.Join(' ', command.Select(Quote));

    /// <summary>What the sandbox runs for one suite: the application first (when the project has a start command), then the suite's own command.</summary>
    public static IReadOnlyList<string> Command(VerifyConfig verify, Gate suite)
    {
        if (verify.Start is null)
        {
            return suite.SandboxCommand;
        }
        var script = new StringBuilder();
        script.AppendLine("app_log=/tmp/qa-app.log");
        script.AppendLine($"( cd {Quote(verify.StartDirectory ?? ".")} && exec {Quote(verify.Start)} ) >\"$app_log\" 2>&1 &");
        script.AppendLine("app=$!");
        script.AppendLine("waited=0");
        script.AppendLine("code=000");
        script.AppendLine($"while [ \"$waited\" -lt {verify.StartupSeconds} ]; do");
        script.AppendLine($"  code=$(curl -s -o /dev/null -m 2 -w '%{{http_code}}' {Quote(verify.Ready!)} 2>/dev/null)");
        script.AppendLine("  case \"$code\" in 000|5??) ;; *) break ;; esac");
        script.AppendLine("  if ! kill -0 \"$app\" 2>/dev/null; then");
        script.AppendLine("    echo 'The application stopped before it was ready:' >&2; tail -n 40 \"$app_log\" >&2; exit " + AppExitedEarly);
        script.AppendLine("  fi");
        script.AppendLine("  sleep 1");
        script.AppendLine("  waited=$((waited + 1))");
        script.AppendLine("done");
        script.AppendLine("case \"$code\" in 000|5??)");
        script.AppendLine($"  echo \"The application was not ready after {verify.StartupSeconds} seconds (last status $code):\" >&2; tail -n 40 \"$app_log\" >&2; kill \"$app\" 2>/dev/null; exit " + AppNotReady);
        script.AppendLine("  ;;");
        script.AppendLine("esac");
        script.AppendLine($"( cd {Quote(suite.WorkingDirectory ?? ".")} && {Quote(suite.Command)} )");
        script.AppendLine("status=$?");
        script.AppendLine("kill \"$app\" 2>/dev/null");
        script.AppendLine("wait \"$app\" 2>/dev/null");
        script.AppendLine("if [ \"$status\" -ne 0 ]; then echo '--- the application log (last lines) ---' >&2; tail -n 30 \"$app_log\" >&2; fi");
        script.AppendLine("exit \"$status\"");
        return ["sh", "-c", script.ToString(), "qa"];
    }

    /// <summary>Prints the files the suites left as evidence (small ones, at most 20, each cut), for the report and the UX review.</summary>
    public static IReadOnlyList<string> ReadEvidence(VerifyConfig verify) =>
        ["sh", "-c",
         "[ -d \"$1\" ] || exit 0\n" +
         "find \"$1\" -type f -size -256k 2>/dev/null | sort | head -n 20 | while IFS= read -r f; do printf '=== %s\\n' \"$f\"; head -c 20000 \"$f\"; printf '\\n'; done",
         "evidence", verify.EvidenceDirectory];

    /// <summary>Removes the evidence, so it is never pushed.</summary>
    public static IReadOnlyList<string> RemoveEvidence(VerifyConfig verify) => ["rm", "-rf", "--", verify.EvidenceDirectory];
}
