namespace AiSdlcFactory.SandboxLauncher;

/// <summary>
/// Fails fast when Docker is unreachable, checks that every egress network exists, and removes sandboxes left behind by a previous
/// launcher process that died mid-run.
/// </summary>
public sealed class SandboxJanitor(IDockerClient docker, SandboxSettings settings, ILogger<SandboxJanitor> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await docker.PingAsync(cancellationToken);
        if (settings.Egress == EgressMode.Open)
        {
            logger.LogWarning(
                "EGRESS RESTRICTIONS ARE OFF (SANDBOX_EGRESS_MODE=open): sandboxes that ask for a network get unrestricted internet access, with no allowlist and no log.");
            await docker.EnsureNetworkAsync(settings.OpenNetwork, cancellationToken);
        }
        else
        {
            await CheckEgressNetworksAsync(cancellationToken);
        }

        foreach (var id in await docker.ListSandboxIdsAsync(cancellationToken))
        {
            logger.LogWarning("Removing leftover sandbox container {ContainerId}", id[..Math.Min(12, id.Length)]);
            await docker.RemoveAsync(id, cancellationToken);
        }
    }

    // The networks are created by Compose for the egress proxy. Without them a sandbox would either fail to
    // start or, worse, be given a network nobody intended, so refuse to run.
    private async Task CheckEgressNetworksAsync(CancellationToken cancellationToken)
    {
        foreach (var (profile, network) in settings.Networks)
        {
            if (!await docker.NetworkExistsAsync(network, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"The network \"{network}\" for egress profile \"{profile}\" does not exist. Start the egress proxy first (docker compose up -d egress-proxy); it creates the networks.");
            }
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
