namespace AiSdlcFactory.Controller;

/// <summary>
/// Present while the worker runs; the container healthcheck tests for it. The Temporal
/// worker service fails the host if it cannot connect, so a live host means it is polling.
/// </summary>
public sealed class ReadinessFile : IHostedService
{
    public const string Path = "/tmp/worker-ready";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        File.WriteAllBytes(Path, []);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        File.Delete(Path);
        return Task.CompletedTask;
    }
}
