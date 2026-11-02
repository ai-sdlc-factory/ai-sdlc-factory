using Temporalio.Exceptions;

namespace DevFoundry.SandboxLauncher.Tests;

public sealed class SandboxRunnerTests
{
    private sealed class FakeDocker : IDockerClient
    {
        public List<string> Calls { get; } = [];
        public Exception? CreateFailure { get; set; }
        public Exception? StartFailure { get; set; }
        public TaskCompletionSource<long> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DockerLogs Logs { get; set; } = new("out", "err", false);

        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;

        public Task EnsureNetworkAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public Task<string> CreateAsync(CreateContainerBody body, CancellationToken ct)
        {
            Calls.Add("create");
            return CreateFailure is null ? Task.FromResult("abc123") : Task.FromException<string>(CreateFailure);
        }

        public Task StartAsync(string id, CancellationToken ct)
        {
            Calls.Add("start");
            return StartFailure is null ? Task.CompletedTask : Task.FromException(StartFailure);
        }

        public Task<long> WaitAsync(string id, CancellationToken ct)
        {
            Calls.Add("wait");
            ct.Register(() => Exit.TrySetCanceled(ct));
            return Exit.Task;
        }

        public Task KillAsync(string id, CancellationToken ct)
        {
            Calls.Add("kill");
            Exit.TrySetResult(137);
            return Task.CompletedTask;
        }

        public Task<DockerLogs> LogsAsync(string id, int maxBytes, CancellationToken ct)
        {
            Calls.Add("logs");
            return Task.FromResult(Logs);
        }

        public Task RemoveAsync(string id, CancellationToken ct)
        {
            Calls.Add("remove");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListSandboxIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static SandboxRunner Runner(FakeDocker docker) =>
        new(docker, Support.Settings(), TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task Finished_command_returns_exit_code_and_output_and_removes_the_container()
    {
        var docker = new FakeDocker();
        docker.Exit.SetResult(3);

        var result = await Runner(docker).RunAsync(Support.Valid(), () => { }, CancellationToken.None);

        Assert.Equal(new SandboxRunResult(3, false, "out", "err", false), result);
        Assert.Equal(["create", "start", "wait", "logs", "remove"], docker.Calls);
    }

    [Fact]
    public async Task Command_that_outlives_its_timeout_is_killed_and_reported()
    {
        var docker = new FakeDocker();
        var run = Support.Valid(Support.Request(timeout: 1));
        var beats = 0;

        var result = await Runner(docker).RunAsync(run, () => beats++, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(137, result.ExitCode);
        Assert.True(beats > 0);
        Assert.Contains("kill", docker.Calls);
        Assert.Equal("remove", docker.Calls[^1]);
    }

    [Fact]
    public async Task Cancelling_the_activity_still_removes_the_container()
    {
        var docker = new FakeDocker();
        using var cancellation = new CancellationTokenSource();
        var running = Runner(docker).RunAsync(Support.Valid(), () => { }, cancellation.Token);
        await Task.Delay(100);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal("remove", docker.Calls[^1]);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound, true)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, false)]
    public async Task Create_failures_are_retried_only_when_the_server_failed(System.Net.HttpStatusCode status, bool nonRetryable)
    {
        var docker = new FakeDocker { CreateFailure = new DockerException(status, "no such image") };

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => Runner(docker).RunAsync(Support.Valid(), () => { }, CancellationToken.None));

        Assert.Equal(nonRetryable, error.NonRetryable);
        Assert.DoesNotContain("remove", docker.Calls);
    }

    [Fact]
    public async Task Failed_start_removes_the_created_container()
    {
        var docker = new FakeDocker { StartFailure = new DockerException(System.Net.HttpStatusCode.InternalServerError, "boom") };

        await Assert.ThrowsAsync<ApplicationFailureException>(
            () => Runner(docker).RunAsync(Support.Valid(), () => { }, CancellationToken.None));

        Assert.Equal(["create", "start", "remove"], docker.Calls);
    }
}
