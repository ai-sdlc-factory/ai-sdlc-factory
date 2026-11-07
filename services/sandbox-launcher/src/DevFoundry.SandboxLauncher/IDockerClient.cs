using System.Net;

namespace DevFoundry.SandboxLauncher;

public sealed class DockerException(HttpStatusCode status, string message)
    : Exception($"Docker returned {(int)status}: {message}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>The few Docker operations the launcher uses; faked in unit tests.</summary>
public interface IDockerClient
{
    Task PingAsync(CancellationToken cancellationToken);
    Task<bool> NetworkExistsAsync(string name, CancellationToken cancellationToken);
    Task EnsureNetworkAsync(string name, CancellationToken cancellationToken);
    Task<bool> VolumeExistsAsync(string name, CancellationToken cancellationToken);
    Task<string> CreateAsync(CreateContainerBody body, CancellationToken cancellationToken);
    Task StartAsync(string id, CancellationToken cancellationToken);
    Task<long> WaitAsync(string id, CancellationToken cancellationToken);
    Task KillAsync(string id, CancellationToken cancellationToken);
    Task<DockerLogs> LogsAsync(string id, int maxBytes, CancellationToken cancellationToken);
    Task RemoveAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListSandboxIdsAsync(CancellationToken cancellationToken);
    Task<DockerImage?> InspectImageAsync(string name, CancellationToken cancellationToken);
    Task BuildImageAsync(Stream context, string tag, DockerBuildLimits limits, IReadOnlyDictionary<string, string> labels, Action<string> onLine, CancellationToken cancellationToken);
    Task RemoveImageAsync(string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<DockerImageListing>> ListRepoImagesAsync(CancellationToken cancellationToken);

    /// <summary>Removes the image unless a container uses it (not forced). False means it is in use.</summary>
    Task<bool> RemoveUnusedImageAsync(string name, CancellationToken cancellationToken);
}

public sealed record DockerImageListing(string Tag, DateTimeOffset Created);

public sealed record DockerImage(string Id, long Size, IReadOnlyDictionary<string, string> Labels);

/// <summary>What one image build may use.</summary>
public sealed record DockerBuildLimits(long MemoryBytes, double Cpus);

public sealed record DockerLogs(string Stdout, string Stderr, bool Truncated);
