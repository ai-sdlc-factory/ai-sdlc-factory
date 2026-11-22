using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using Temporalio.Exceptions;

namespace AiSdlcFactory.SandboxLauncher;

/// <summary>Builds a repository's image once per distinct content, with limits, and reports what happened.</summary>
public sealed class RepoImageBuilder(IDockerClient docker, SandboxSettings settings)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    public async Task<SandboxBuildResult> BuildAsync(SandboxBuildRequest request, Action onHeartbeat, CancellationToken cancellationToken)
    {
        if (!settings.AllowRepoImages)
        {
            throw RepoImagePolicy.Reject("repository images are switched off in this factory (FACTORY_ALLOW_REPO_IMAGES).");
        }
        var build = RepoImagePolicy.Validate(request, settings.Images.Values.ToList());
        var baseImage = await docker.InspectImageAsync(build.BaseImage, cancellationToken)
            ?? throw RepoImagePolicy.Reject($"the base image {build.BaseImage} has not been built on this host.");
        var hash = RepoImagePolicy.Hash(baseImage.Id, build);
        var alias = RepoImageNames.AliasFor(hash);
        var tag = RepoImageNames.TagFor(alias);

        var gate = Locks.GetOrAdd(tag, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await docker.InspectImageAsync(tag, cancellationToken) is { } existing && existing.Labels.ContainsKey(RepoImageNames.Label))
            {
                return new SandboxBuildResult(alias, false, 0, existing.Size, "");
            }

            var clock = Stopwatch.StartNew();
            var tail = new Queue<string>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 60, settings.MaxBuildSeconds)));
            try
            {
                await docker.BuildImageAsync(
                    BuildContext(build), tag, new DockerBuildLimits(settings.MemoryBytes * 2, settings.Cpus),
                    new Dictionary<string, string> { [RepoImageNames.Label] = "true", ["ai-sdlc-factory.repo-image-hash"] = hash },
                    line =>
                    {
                        onHeartbeat();
                        foreach (var part in line.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            tail.Enqueue(part.TrimEnd());
                            while (tail.Count > 60)
                            {
                                tail.Dequeue();
                            }
                        }
                    },
                    timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await docker.RemoveImageAsync(tag, CancellationToken.None);
                throw Failed($"the build did not finish in time.", tail);
            }
            catch (DockerException error)
            {
                throw Failed($"the build failed: {error.Message}", tail);
            }

            var info = await docker.InspectImageAsync(tag, cancellationToken) ?? throw Failed("the build finished but the image is missing.", tail);
            if (info.Size > settings.MaxRepoImageBytes)
            {
                await docker.RemoveImageAsync(tag, CancellationToken.None);
                throw Failed($"the image is {info.Size / (1024 * 1024)} MB, over the limit of {settings.MaxRepoImageBytes / (1024 * 1024)} MB.", tail);
            }
            return new SandboxBuildResult(alias, true, Math.Round(clock.Elapsed.TotalSeconds, 1), info.Size, Tail(tail));
        }
        finally
        {
            gate.Release();
        }
    }

    private static MemoryStream BuildContext(ValidatedBuild build)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            void Add(string name, byte[] content, UnixFileMode mode)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(content),
                    Mode = mode,
                    ModificationTime = DateTimeOffset.UnixEpoch,
                });
            }
            Add("Dockerfile", Encoding.UTF8.GetBytes(build.Dockerfile), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            foreach (var (path, content) in build.Files)
            {
                Add(path, content, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static string Tail(Queue<string> lines)
    {
        var text = string.Join('\n', lines.Where(line => !line.StartsWith(" ---> ", StringComparison.Ordinal) && !line.StartsWith("Removing intermediate", StringComparison.Ordinal)));
        return text.Length > 1800 ? text[^1800..] : text;
    }

    private static ApplicationFailureException Failed(string message, Queue<string> tail) =>
        new($"Repository image build: {message}\n{Tail(tail)}".TrimEnd(), errorType: "RepoImageBuild", nonRetryable: true);
}
