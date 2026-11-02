using System.Diagnostics;
using Temporalio.Exceptions;

namespace DevFoundry.SandboxLauncher;

/// <summary>Runs one validated command in a disposable container and always removes the container.</summary>
public sealed class SandboxRunner(IDockerClient docker, SandboxSettings settings, TimeSpan? heartbeatInterval = null)
{
    private readonly TimeSpan heartbeat = heartbeatInterval ?? TimeSpan.FromSeconds(5);

    public async Task<SandboxRunResult> RunAsync(
        ValidatedRun run, Action onHeartbeat, CancellationToken cancellationToken)
    {
        string id;
        try
        {
            id = await docker.CreateAsync(ContainerSpec.Build(run, settings), cancellationToken);
        }
        catch (DockerException error)
        {
            throw Failure($"creating the sandbox failed: {error.Message}", error, retryable: (int)error.Status >= 500);
        }

        try
        {
            try
            {
                await docker.StartAsync(id, cancellationToken);
            }
            catch (DockerException error)
            {
                throw Failure($"starting the sandbox failed: {error.Message}", error, retryable: (int)error.Status >= 500);
            }

            // From here on the command may have run, so failures are never retried.
            try
            {
                var waiting = docker.WaitAsync(id, cancellationToken);
                var clock = Stopwatch.StartNew();
                var timedOut = false;
                while (!waiting.IsCompleted)
                {
                    await Task.WhenAny(waiting, Task.Delay(heartbeat, cancellationToken));
                    onHeartbeat();
                    if (!waiting.IsCompleted && clock.Elapsed >= run.Timeout)
                    {
                        timedOut = true;
                        await docker.KillAsync(id, cancellationToken);
                        break;
                    }
                }

                var exitCode = await waiting;
                var logs = await docker.LogsAsync(id, settings.MaxOutputBytes, cancellationToken);
                return new SandboxRunResult(exitCode, timedOut, logs.Stdout, logs.Stderr, logs.Truncated);
            }
            catch (DockerException error)
            {
                throw Failure($"running the sandbox failed: {error.Message}", error, retryable: false);
            }
        }
        finally
        {
            // Cleanup must happen even when the activity was cancelled.
            await docker.RemoveAsync(id, CancellationToken.None);
        }
    }

    private static ApplicationFailureException Failure(string message, Exception inner, bool retryable) =>
        new(message, inner, errorType: "Docker", nonRetryable: !retryable);
}
