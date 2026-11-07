namespace DevFoundry.SandboxLauncher;

/// <summary>
/// Removes repository images (the ones we built from a <c>.factory/Dockerfile</c>) that are old and unused. Only images that carry our label and
/// live under our repository name are ever touched, and an image a container still uses is left alone.
/// </summary>
public sealed class RepoImageCleaner(IDockerClient docker, SandboxSettings settings, ILogger<RepoImageCleaner> log, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<IReadOnlyList<string>> RunOnceAsync(CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        if (settings.JanitorImageDays == 0)
        {
            return removed;
        }
        foreach (var image in await docker.ListRepoImagesAsync(cancellationToken))
        {
            if (now() - image.Created < TimeSpan.FromDays(settings.JanitorImageDays))
            {
                continue;
            }
            try
            {
                if (await docker.RemoveUnusedImageAsync(image.Tag, cancellationToken))
                {
                    removed.Add(image.Tag);
                    log.LogInformation("Janitor: removed the repository image {Tag} (built {Created:u}); it is rebuilt if a repository needs it again", image.Tag, image.Created);
                }
                else
                {
                    log.LogInformation("Janitor: kept {Tag}: a container still uses it", image.Tag);
                }
            }
            catch (DockerException error)
            {
                log.LogWarning(error, "Janitor: could not remove {Tag}", image.Tag);
            }
        }
        return removed;
    }
}

/// <summary>Runs <see cref="RepoImageCleaner"/> daily, but only while repository images are switched on.</summary>
public sealed class RepoImageJanitor(RepoImageCleaner cleaner, SandboxSettings settings, ILogger<RepoImageJanitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.AllowRepoImages || settings.JanitorImageDays == 0)
        {
            return;
        }
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            do
            {
                try
                {
                    await cleaner.RunOnceAsync(stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log.LogWarning(error, "Janitor pass over the repository images failed; trying again tomorrow");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
