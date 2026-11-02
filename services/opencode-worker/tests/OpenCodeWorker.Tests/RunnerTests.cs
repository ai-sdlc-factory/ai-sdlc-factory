using System.Runtime.Versioning;
using Temporalio.Exceptions;

namespace OpenCodeWorker.Tests;

[SupportedOSPlatform("linux")]
public sealed class RunnerTests
{
    private static readonly OpenCodeTask Task = new("test-task", "Create the demo files.");

    [Fact]
    public async Task Runs_without_shell_and_excludes_control_plane_secrets()
    {
        const string script = """
            [ "$1 $2 $3 $4 $5" = "--print-logs run --format json --model" ] || exit 11
            for last; do :; done
            [ "$last" = "Create the demo files." ] || exit 12
            [ -z "${TEMPORAL_ADDRESS+x}" ] || exit 13
            [ -z "${FORGEJO_WEBHOOK_SECRET+x}" ] || exit 14
            [ -z "${POSTGRES_PASSWORD+x}" ] || exit 15
            printf "def greet(name): return 'Hello, ' + name + '!'" > hello.py
            echo "# example test" > test_hello.py
            echo '{"type":"text","part":{"text":"Created both files."}}'
            """;
        using var fake = new FakeOpenCode(script);
        var variables = new[] { "TEMPORAL_ADDRESS", "FORGEJO_WEBHOOK_SECRET", "POSTGRES_PASSWORD" };
        foreach (var name in variables)
        {
            Environment.SetEnvironmentVariable(name, "must-not-be-forwarded");
        }
        try
        {
            var result = await OpenCodeRunner.ExecuteTaskAsync(Task, fake.Settings, fake.Executable);

            Assert.Equal("Created both files.", result.Summary);
            Assert.Equal(["hello.py", "test_hello.py"], result.Artifacts.Keys.Order());
            Assert.Equal("test-task", result.TaskId);
        }
        finally
        {
            foreach (var name in variables)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    [Fact]
    public async Task Requires_login_before_launching_opencode()
    {
        var settings = new Settings { OpenCodeDataHome = Path.GetTempPath() + "/no-such-data-home" };
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => OpenCodeRunner.ExecuteTaskAsync(Task, settings, "missing"));
        Assert.Contains("not signed in", error.Message);
    }

    [Fact]
    public async Task Timeout_stops_the_subprocess()
    {
        using var fake = new FakeOpenCode("sleep 30\n", timeoutSeconds: 1);
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => OpenCodeRunner.ExecuteTaskAsync(Task, fake.Settings, fake.Executable));
        Assert.Contains("timed out", error.Message);
    }

    [Fact]
    public async Task Error_event_is_not_success()
    {
        using var fake = new FakeOpenCode("""echo '{"type":"error"}'""");
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => OpenCodeRunner.ExecuteTaskAsync(Task, fake.Settings, fake.Executable));
        Assert.Contains("reported an error", error.Message);
    }

    [Fact]
    public async Task Artifact_symlink_is_rejected()
    {
        using var fake = new FakeOpenCode("ln -s /etc/passwd hello.py\necho '# test' > test_hello.py\n");
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => OpenCodeRunner.ExecuteTaskAsync(Task, fake.Settings, fake.Executable));
        Assert.Contains("regular hello.py", error.Message);
    }

    [Fact]
    public async Task Missing_artifacts_fail_the_task()
    {
        using var fake = new FakeOpenCode("echo '{}'\n");
        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => OpenCodeRunner.ExecuteTaskAsync(Task, fake.Settings, fake.Executable));
        Assert.Contains("did not create", error.Message);
    }

    [Fact]
    public async Task Failure_diagnostics_redact_credentials()
    {
        const string token = "test-private-credential-value";
        using var fake = new FakeOpenCode(
            $"echo 'ERROR startup failure {token}' >&2\nexit 1\n",
            authJson: $$$"""{"openai": {"access": "{{{token}}}"}}""");

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => OpenCodeRunner.ExecuteTaskAsync(Task, fake.Settings, fake.Executable));

        Assert.DoesNotContain(token, error.Message);
        Assert.Contains("[REDACTED]", error.Message);
        Assert.Contains("startup failure", error.Message);
    }

    [Fact]
    public void Model_must_be_an_openai_model()
    {
        Assert.Throws<InvalidOperationException>(() => new Settings { OpenCodeModel = "other/model" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new Settings { OpenCodeTimeoutSeconds = 241 }.Validate());
    }
}
