using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Temporalio.Exceptions;

namespace DevFoundry.SandboxLauncher;

/// <summary>Names of images built from a repository: the alias a run uses and the Docker tag behind it.</summary>
public static partial class RepoImageNames
{
    public const string Repository = "dev-foundry/repo";
    public const string Label = "dev-foundry.repo-image";

    [GeneratedRegex(@"\Arepo-[0-9a-f]{12}\z")]
    private static partial Regex AliasPattern();

    public static bool IsAlias(string? alias) => alias is not null && AliasPattern().IsMatch(alias);
    public static string AliasFor(string hash) => $"repo-{hash}";
    public static string TagFor(string alias) => $"{Repository}:{alias["repo-".Length..]}";
}

/// <summary>A Dockerfile and context that passed the policy.</summary>
public sealed record ValidatedBuild(string Dockerfile, string BaseImage, IReadOnlyList<(string Path, byte[] Content)> Files);

/// <summary>
/// What a repository's Dockerfile may contain. The file is untrusted text that will run on this host's Docker, so the policy is narrow: it must
/// start from one of the factory's own images (which guarantees git, the agent and the entrypoint are there), use a short list of instructions
/// that cannot change how the container starts, and fetch nothing at build time except through RUN.
/// </summary>
public static partial class RepoImagePolicy
{
    public const int MaxDockerfileBytes = 64 * 1024;
    public const int MaxInstructions = 200;
    public const int MaxFiles = 50;
    public const int MaxContextBytes = 1024 * 1024;
    private static readonly string[] AllowedInstructions = ["RUN", "COPY", "ADD", "ENV", "ARG", "LABEL"];

    [GeneratedRegex(@"\A[A-Za-z0-9._@+/-]{1,200}\z")]
    private static partial Regex FilePath();

    [GeneratedRegex(@"^\s*#\s*(syntax|escape|check)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ParserDirective();

    public static ValidatedBuild Validate(SandboxBuildRequest request, IReadOnlyCollection<string> allowedBaseImages)
    {
        var text = request.Dockerfile ?? "";
        if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > MaxDockerfileBytes || text.Contains('\0'))
        {
            throw Reject($"the Dockerfile must be 1 byte to {MaxDockerfileBytes / 1024} KB of text.");
        }

        var instructions = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (ParserDirective().IsMatch(raw))
            {
                throw Reject("parser directives such as '# syntax=' are not allowed.");
            }
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue; // blank lines and comments also inside a continued instruction
            }
            if (line.EndsWith('\\'))
            {
                current.Append(line.AsSpan(0, line.Length - 1)).Append(' ');
                continue;
            }
            current.Append(line);
            instructions.Add(current.ToString().Trim());
            current.Clear();
        }
        if (current.Length > 0)
        {
            instructions.Add(current.ToString().Trim());
        }
        if (instructions.Count == 0 || instructions.Count > MaxInstructions)
        {
            throw Reject($"the Dockerfile must have 1 to {MaxInstructions} instructions.");
        }

        var first = instructions[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!first[0].Equals("FROM", StringComparison.OrdinalIgnoreCase) || first.Length != 2 || !allowedBaseImages.Contains(first[1]))
        {
            throw Reject($"the first instruction must be exactly 'FROM <image>' with one of the factory's images: {string.Join(", ", allowedBaseImages.Order())}.");
        }

        foreach (var instruction in instructions.Skip(1))
        {
            var parts = instruction.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var keyword = parts[0].ToUpperInvariant();
            if (!AllowedInstructions.Contains(keyword))
            {
                throw Reject($"the instruction {keyword} is not allowed (allowed: {string.Join(", ", AllowedInstructions)}; one FROM, first).");
            }
            var flags = parts.Skip(1).TakeWhile(part => part.StartsWith("--", StringComparison.Ordinal)).Select(part => part.ToLowerInvariant()).ToList();
            if (keyword is "COPY" or "ADD")
            {
                if (flags.Any(flag => !flag.StartsWith("--chown=", StringComparison.Ordinal) && !flag.StartsWith("--chmod=", StringComparison.Ordinal)))
                {
                    throw Reject($"{keyword} may only use the flags --chown and --chmod (not --from).");
                }
                if (keyword == "ADD" && parts.Skip(1).Any(part => part.Contains("://", StringComparison.Ordinal) || part.StartsWith("git@", StringComparison.Ordinal)))
                {
                    throw Reject("ADD may not fetch a URL; use RUN with the tool you trust, or COPY a file from .factory/docker/.");
                }
            }
            else if (keyword == "RUN" && flags.Count > 0)
            {
                throw Reject("RUN may not use flags such as --mount, --network or --security.");
            }
        }

        var files = new List<(string Path, byte[] Content)>();
        long total = 0;
        foreach (var (path, encoded) in request.Files ?? new Dictionary<string, string>())
        {
            if (!FilePath().IsMatch(path) || path.StartsWith('/') || path.EndsWith('/') || path.Split('/').Any(segment => segment is "" or "." or "..")
                || path.Equals("Dockerfile", StringComparison.Ordinal))
            {
                throw Reject($"the context file '{path}' has an unusable path.");
            }
            byte[] content;
            try
            {
                content = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                throw Reject($"the context file '{path}' is not valid base64.");
            }
            total += content.Length;
            files.Add((path, content));
        }
        if (files.Count > MaxFiles || total > MaxContextBytes)
        {
            throw Reject($"the build context may have at most {MaxFiles} files and {MaxContextBytes / 1024} KB.");
        }

        return new ValidatedBuild(text, first[1], files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList());
    }

    /// <summary>Twelve hex characters that change when the base image, the Dockerfile or any context file changes.</summary>
    public static string Hash(string baseImageId, ValidatedBuild build)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string part) => sha.AppendData(Encoding.UTF8.GetBytes(part + "\0"));
        Add(baseImageId);
        Add(build.Dockerfile);
        foreach (var (path, content) in build.Files)
        {
            Add(path);
            Add(Convert.ToHexString(SHA256.HashData(content)));
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant();
    }

    // A build that will not fix itself on a retry.
    public static ApplicationFailureException Reject(string message) =>
        new($"Repository image rejected: {message}", errorType: "RepoImagePolicy", nonRetryable: true);
}

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
                    new Dictionary<string, string> { [RepoImageNames.Label] = "true", ["dev-foundry.repo-image-hash"] = hash },
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
