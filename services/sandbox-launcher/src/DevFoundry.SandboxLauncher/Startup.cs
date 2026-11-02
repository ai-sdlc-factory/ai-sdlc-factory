namespace DevFoundry.SandboxLauncher;

/// <summary>
/// Fails fast when Docker is unreachable, makes sure the sandbox network exists, and removes sandboxes left behind by a previous
/// launcher process that died mid-run.
/// </summary>
public sealed class SandboxJanitor(IDockerClient docker, SandboxSettings settings, ILogger<SandboxJanitor> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await docker.PingAsync(cancellationToken);
        await docker.EnsureNetworkAsync(settings.Network, cancellationToken);
        foreach (var id in await docker.ListSandboxIdsAsync(cancellationToken))
        {
            logger.LogWarning("Removing leftover sandbox container {ContainerId}", id[..Math.Min(12, id.Length)]);
            await docker.RemoveAsync(id, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Present while the worker runs; the container healthcheck tests for it.</summary>
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
