using System.Diagnostics;
using Xunit;

namespace DevFoundry.Controller.Tests;

public sealed class VerifyScriptTests
{
    private static VerifyConfig Config(string verify)
    {
        var json = $$"""{"image":"node","gates":[{"name":"t","command":["npm","test"]}],"verify":{{verify}}}""";
        Assert.True(ProjectConfigParser.TryParse(json, out var config, out var error), error);
        return config!.Verify!;
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("with space", "'with space'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$(rm -rf /); `x`", "'$(rm -rf /); `x`'")]
    [InlineData("", "''")]
    public void A_value_is_quoted_so_that_nothing_in_it_is_shell_syntax(string value, string expected)
    {
        Assert.Equal(expected, VerifyScript.Quote(value));
    }

    [Fact]
    public void Without_a_start_command_the_suite_runs_as_a_gate_does()
    {
        var verify = Config("""{"tests":"e2e","suites":[{"name":"api","command":["npm","run","e2e"],"cwd":"e2e"}]}""");

        Assert.Equal(verify.Suites[0].SandboxCommand, VerifyScript.Command(verify, verify.Suites[0]));
    }

    [Fact]
    public void With_a_start_command_one_script_starts_the_application_waits_runs_the_suite_and_stops_it()
    {
        var verify = Config("""{"start":["dotnet","run"],"start_cwd":"src/Api","ready":"http://127.0.0.1:5080/health","startup_timeout_seconds":45,"tests":"e2e","suites":[{"name":"api","command":["dotnet","test"],"cwd":"e2e/api"}]}""");

        var command = VerifyScript.Command(verify, verify.Suites[0]);

        Assert.Equal(["sh", "-c"], command.Take(2));
        Assert.Equal("qa", command[3]);
        var script = command[2];
        Assert.Contains("( cd 'src/Api' && exec 'dotnet' 'run' ) >\"$app_log\" 2>&1 &", script);
        Assert.Contains("curl -s -o /dev/null -m 2 -w '%{http_code}' 'http://127.0.0.1:5080/health'", script);
        Assert.Contains("-lt 45 ]", script);
        Assert.Contains("( cd 'e2e/api' && 'dotnet' 'test' )", script);
        Assert.Contains($"exit {VerifyScript.AppExitedEarly}", script);
        Assert.Contains($"exit {VerifyScript.AppNotReady}", script);
        Assert.Contains("kill \"$app\"", script);
    }

    [Fact]
    public void A_hostile_argument_cannot_break_out_of_the_script()
    {
        var verify = Config("""{"start":["node","a'; touch /tmp/pwned; '"],"ready":"http://127.0.0.1:80/","tests":"e2e","suites":[{"name":"api","command":["x"]}]}""");

        var script = VerifyScript.Command(verify, verify.Suites[0])[2];

        Assert.Contains("'a'\\''; touch /tmp/pwned; '\\'''", script);
    }

    [Fact]
    public void Evidence_is_read_from_and_removed_with_the_same_folder()
    {
        var verify = Config("""{"tests":"e2e","suites":[{"name":"ui","command":["x"]}]}""");

        Assert.Equal("e2e/evidence", VerifyScript.ReadEvidence(verify)[^1]);
        Assert.Equal(["rm", "-rf", "--", "e2e/evidence"], VerifyScript.RemoveEvidence(verify));
    }

    private static bool Has(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            process!.WaitForExit(5000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (int Code, string Output) Run(IReadOnlyList<string> command)
    {
        var info = new ProcessStartInfo(command[0]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in command.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "the script did not finish");
        return (process.ExitCode, output.Result + error.Result);
    }

    /// <summary>
    /// A listener on a port the operating system just said was free. A port worked out from the process id sat in the range outgoing connections use, so it could
    /// be taken by another test's socket; and even a free port can be taken between asking and listening, so a failed start tries another.
    /// </summary>
    private static (System.Net.HttpListener Listener, int Port) StartListenerOnFreePort()
    {
        for (var attempt = 1; ; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new System.Net.HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (System.Net.HttpListenerException) when (attempt < 10)
            {
                try
                {
                    listener.Close();
                }
                catch (System.Net.HttpListenerException)
                {
                    // Closing a listener that never started can fail the same way; the next try uses another port.
                }
            }
        }
    }

    [SkippableFact]
    public void The_script_really_waits_for_the_application_and_returns_the_suites_exit_code()
    {
        Skip.IfNot(Has("curl"), "needs curl");
        // The "application" is a listener in this process; the started command only has to stay alive until the script stops it.
        var (listener, port) = StartListenerOnFreePort();
        using var _ = listener;
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                }
                catch (Exception)
                {
                    return;
                }
            }
        });
        var verify = Config($$$"""{"start":["sleep","30"],"ready":"http://127.0.0.1:{{{port}}}/","startup_timeout_seconds":20,"tests":"e2e","suites":[{"name":"api","command":["sh","-c","curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:{{{port}}}/ | grep -q 200 && exit 3"]}]}""");

        var (code, _) = Run(VerifyScript.Command(verify, verify.Suites[0]));
        listener.Stop();

        Assert.Equal(3, code); // the suite ran once the application answered, and its own exit code came back
    }

    [SkippableFact]
    public void An_application_that_stops_at_once_is_reported_with_its_log()
    {
        Skip.IfNot(Has("curl"), "needs curl");
        var verify = Config("""{"start":["sh","-c","echo boom from the app; exit 1"],"ready":"http://127.0.0.1:19/","startup_timeout_seconds":20,"tests":"e2e","suites":[{"name":"api","command":["true"]}]}""");

        var (code, output) = Run(VerifyScript.Command(verify, verify.Suites[0]));

        Assert.Equal(VerifyScript.AppExitedEarly, code);
        Assert.Contains("boom from the app", output);
    }

    [SkippableFact]
    public void An_application_that_never_answers_gives_up_after_the_startup_time()
    {
        Skip.IfNot(Has("curl"), "needs curl");
        var verify = Config("""{"start":["sleep","60"],"ready":"http://127.0.0.1:19/","startup_timeout_seconds":5,"tests":"e2e","suites":[{"name":"api","command":["true"]}]}""");

        var (code, output) = Run(VerifyScript.Command(verify, verify.Suites[0]));

        Assert.Equal(VerifyScript.AppNotReady, code);
        Assert.Contains("not ready after 5 seconds", output);
    }
}
