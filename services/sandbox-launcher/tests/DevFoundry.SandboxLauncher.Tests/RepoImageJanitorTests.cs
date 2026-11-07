namespace DevFoundry.SandboxLauncher.Tests;

public sealed class RepoImageCleanerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class Docker : IDockerClient
    {
        public List<DockerImageListing> Listing { get; } = [];
        public HashSet<string> InUse { get; } = [];
        public List<string> Removed { get; } = [];
        public Task<IReadOnlyList<DockerImageListing>> ListRepoImagesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DockerImageListing>>(Listing);
        public Task<bool> RemoveUnusedImageAsync(string name, CancellationToken ct)
        {
            if (InUse.Contains(name)) { return Task.FromResult(false); }
            Removed.Add(name);
            return Task.FromResult(true);
        }

        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<bool> NetworkExistsAsync(string name, CancellationToken ct) => Task.FromResult(true);
        public Task EnsureNetworkAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> VolumeExistsAsync(string name, CancellationToken ct) => Task.FromResult(true);
        public Task<string> CreateAsync(CreateContainerBody body, CancellationToken ct) => throw new NotSupportedException();
        public Task StartAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> WaitAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task KillAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<DockerLogs> LogsAsync(string id, int maxBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSandboxIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<DockerImage?> InspectImageAsync(string name, CancellationToken ct) => Task.FromResult<DockerImage?>(null);
        public Task BuildImageAsync(Stream context, string tag, DockerBuildLimits limits, IReadOnlyDictionary<string, string> labels, Action<string> onLine, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private static (Docker Docker, RepoImageCleaner Cleaner) Make(int days = 60)
    {
        var docker = new Docker();
        var settings = new SandboxSettings
        {
            WorkspaceRoot = "/work", Images = new Dictionary<string, string> { ["a"] = "b" }, Networks = new Dictionary<string, string>(),
            Proxies = new Dictionary<string, string>(), AllowRepoImages = true, JanitorImageDays = days,
        };
        return (docker, new RepoImageCleaner(docker, settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<RepoImageCleaner>.Instance, () => Now));
    }

    [Fact]
    public async Task Only_images_older_than_the_limit_are_removed()
    {
        var (docker, cleaner) = Make(days: 60);
        docker.Listing.Add(new("dev-foundry/repo:old000000001", Now.AddDays(-61)));
        docker.Listing.Add(new("dev-foundry/repo:young0000002", Now.AddDays(-59)));

        var removed = await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["dev-foundry/repo:old000000001"], removed);
        Assert.Equal(["dev-foundry/repo:old000000001"], docker.Removed);
    }

    [Fact]
    public async Task An_image_a_container_still_uses_is_kept()
    {
        var (docker, cleaner) = Make();
        docker.Listing.Add(new("dev-foundry/repo:busy00000001", Now.AddDays(-90)));
        docker.InUse.Add("dev-foundry/repo:busy00000001");

        Assert.Empty(await cleaner.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Zero_days_keeps_everything_forever()
    {
        var (docker, cleaner) = Make(days: 0);
        docker.Listing.Add(new("dev-foundry/repo:old000000001", Now.AddDays(-900)));

        Assert.Empty(await cleaner.RunOnceAsync(CancellationToken.None));
        Assert.Empty(docker.Removed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public void A_retention_outside_zero_to_365_stops_the_start(int days)
    {
        var settings = new SandboxSettings
        {
            WorkspaceRoot = "/work", Images = new Dictionary<string, string> { ["a"] = "b" }, Networks = new Dictionary<string, string>(),
            Proxies = new Dictionary<string, string>(), JanitorImageDays = days,
        };

        Assert.Throws<InvalidOperationException>(() => settings.Validate());
    }
}
