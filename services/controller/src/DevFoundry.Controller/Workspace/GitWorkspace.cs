using System.Text.RegularExpressions;
using Temporalio.Exceptions;

namespace DevFoundry.Controller;

/// <summary><see cref="ExistingBranch"/> checks out a branch that already exists on the remote (a pull request being revised) instead of starting a new one.</summary>
public sealed record WorkspaceRequest(string Owner, string Repo, string Name, string Branch, bool ExistingBranch = false);

public sealed record WorkspaceInfo(string Name, string Path, string Branch, string DefaultBranch, string HeadCommit);

/// <summary>
/// Creates and removes the per-run git checkouts that sandboxes mount. Only the controller does
/// this, because only it holds the Forgejo token; the token is handed to git through the
/// environment, so it appears in no argument list and is never written to the repository config.
/// </summary>
public sealed partial class GitWorkspace(WorkerSettings settings, Func<string, string, string>? remoteUrl = null) : IWorkspaceStore
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(3);

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,100}\z")]
    private static partial Regex SafeSegment();

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9._-]{0,127}\z")]
    private static partial Regex WorkspaceName();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._/-]{0,100}\z")]
    private static partial Regex BranchName();

    private const int MaxChangedFiles = 300;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const long MaxTotalBytes = 10 * 1024 * 1024;
    private const int MaxProjectConfigBytes = 16 * 1024;

    // The agent works in <root>/<name>. <root>/.base/<name> is a pristine clone no sandbox ever mounts:
    // git (hooks, config, credentials) only ever runs there, never inside the agent's writable copy.
    private string WorkPath(string root, string name) => $"{root}/{name}";

    private string BasePath(string root, string name) => $"{root}/.base/{name}";

    public async Task<WorkspaceInfo> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        if (!SafeSegment().IsMatch(request.Owner) || !SafeSegment().IsMatch(request.Repo)
            || request.Repo.Contains("..") || request.Owner.Contains("..")
            || !WorkspaceName().IsMatch(request.Name) || request.Name.Contains("..")
            || !BranchName().IsMatch(request.Branch) || request.Branch.Contains(".."))
        {
            throw new ApplicationFailureException("Workspace request has an invalid name.", nonRetryable: true);
        }

        var work = WorkPath(root, request.Name);
        var basePath = BasePath(root, request.Name);
        // A retry after a half-finished attempt starts from a clean directory.
        DeleteDirectory(work);
        DeleteDirectory(basePath);
        Directory.CreateDirectory(basePath);

        var url = remoteUrl?.Invoke(request.Owner, request.Repo)
            ?? $"{settings.ForgejoUrl.TrimEnd('/')}/{request.Owner}/{request.Repo}.git";
        await GitAsync(["clone", "--quiet", url, "."], basePath, useToken: true, cancellationToken);
        if ((await GitAsync(["rev-parse", "--verify", "--quiet", "HEAD"], basePath, useToken: false, cancellationToken, allowFailure: true)).Trim().Length == 0)
        {
            // A brand-new repository has nothing to branch from, and no .factory/project.json to read. Retrying cannot help.
            throw new ApplicationFailureException(
                $"The repository {request.Owner}/{request.Repo} is empty: it has no commits yet. Make a first commit on its default branch (a README and a .factory/project.json), then close and reopen the ticket.",
                nonRetryable: true);
        }
        if (request.ExistingBranch)
        {
            if ((await GitAsync(["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{request.Branch}"], basePath, useToken: false, cancellationToken, allowFailure: true)).Trim().Length == 0)
            {
                throw new ApplicationFailureException($"The branch {request.Branch} does not exist on the remote.", nonRetryable: true);
            }
            await GitAsync(["checkout", "--quiet", "-b", request.Branch, $"origin/{request.Branch}"], basePath, useToken: false, cancellationToken);
        }
        else
        {
            await GitAsync(["checkout", "--quiet", "-b", request.Branch], basePath, useToken: false, cancellationToken);
        }
        await GitAsync(["config", "user.name", "Dev Foundry"], basePath, useToken: false, cancellationToken);
        await GitAsync(["config", "user.email", "factory-bot@dev-foundry.test"], basePath, useToken: false, cancellationToken);

        var head = (await GitAsync(["rev-parse", "HEAD"], basePath, useToken: false, cancellationToken)).Trim();
        // Where this run started, so every inspection can put the pristine clone back there: the tip of the default branch for
        // a new ticket, the tip of the pull request's branch for a revision.
        File.WriteAllText($"{basePath}/.git/factory-start", head);
        var defaultBranch = (await GitAsync(["rev-parse", "--abbrev-ref", "origin/HEAD"], basePath, useToken: false, cancellationToken))
            .Trim().Replace("origin/", "", StringComparison.Ordinal);

        // The agent gets a copy (including .git, so it can use git itself); the pristine clone stays untouched.
        Directory.CreateDirectory(work);
        await RunAsync("cp", ["-a", $"{basePath}/.", work], root, cancellationToken);
        return new WorkspaceInfo(request.Name, work, request.Branch, defaultBranch, head);
    }

    /// <summary>The janitor's name for <see cref="CleanupAsync"/>: removes the checkout, the pristine clone and the record folder of one run.</summary>
    public async Task DeleteAsync(string name)
    {
        await CleanupAsync(name);
        ClearResume(name); // the run is long gone and its saved stages are as old as the checkout, so they go with it
    }

    public Task CleanupAsync(string name)
    {
        var root = RequireRoot();
        if (!WorkspaceName().IsMatch(name) || name.Contains(".."))
        {
            throw new ApplicationFailureException("Workspace name is invalid.", nonRetryable: true);
        }
        DeleteDirectory(WorkPath(root, name));
        DeleteDirectory(BasePath(root, name));
        DeleteDirectory(RecordPath(root, name));
        return Task.CompletedTask;
    }

    /// <summary>The project's .factory/project.json from the pristine clone, or null if the repository has none.</summary>
    public string? ReadProjectConfig(string name)
    {
        var root = RequireRoot();
        RequireName(name);
        var file = new FileInfo($"{BasePath(root, name)}/.factory/project.json");
        if (!file.Exists || file.LinkTarget is not null || file.Length > MaxProjectConfigBytes)
        {
            return null;
        }
        return File.ReadAllText(file.FullName);
    }

    /// <summary>
    /// Every per-run folder name under the workspace root (the checkout, the pristine clone and the record folder all carry the run's name) and
    /// when it was last touched: the newest change among the three folders and their direct entries.
    /// </summary>
    public IReadOnlyList<WorkspaceEntry> List()
    {
        var root = RequireRoot();
        var latest = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var parent in new[] { root, $"{root}/.base", $"{root}/.records" })
        {
            var directory = new DirectoryInfo(parent);
            if (!directory.Exists)
            {
                continue;
            }
            foreach (var child in directory.EnumerateDirectories().Where(child => child.LinkTarget is null && !child.Name.StartsWith('.') && WorkspaceName().IsMatch(child.Name)))
            {
                var touched = child.EnumerateFileSystemInfos().Select(entry => entry.LastWriteTimeUtc).Append(child.LastWriteTimeUtc).Max();
                latest[child.Name] = latest.TryGetValue(child.Name, out var earlier) && earlier > touched ? earlier : touched;
            }
        }
        var resumeFolder = new DirectoryInfo($"{root}/.resume");
        if (resumeFolder.Exists && resumeFolder.LinkTarget is null)
        {
            // A ticket's saved stages count as part of its run: the janitor removes them when nothing touched any of it for the configured days.
            foreach (var file in resumeFolder.EnumerateFiles("*.json").Where(file => file.LinkTarget is null))
            {
                var name = Path.GetFileNameWithoutExtension(file.Name);
                if (WorkspaceName().IsMatch(name))
                {
                    var touched = file.LastWriteTimeUtc;
                    latest[name] = latest.TryGetValue(name, out var earlier) && earlier > touched ? earlier : touched;
                }
            }
        }
        return latest.Select(pair => new WorkspaceEntry(pair.Key, new DateTimeOffset(pair.Value, TimeSpan.Zero))).OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList();
    }

    public const int MaxImageContextFiles = 50;
    public const long MaxImageContextBytes = 1024 * 1024;
    private const long MaxDockerfileBytes = 64 * 1024;

    /// <summary>
    /// The repository's own sandbox image definition from the pristine clone, or null if it has none. Only regular files count: a symbolic link
    /// anywhere is ignored (so it cannot point at something outside the checkout), and a context that is too big is refused.
    /// </summary>
    public RepoImageSource? ReadProjectImage(string name)
    {
        var root = RequireRoot();
        RequireName(name);
        var basePath = BasePath(root, name);
        var dockerfile = new FileInfo($"{basePath}/.factory/Dockerfile");
        if (!dockerfile.Exists || dockerfile.LinkTarget is not null)
        {
            return null;
        }
        if (dockerfile.Length > MaxDockerfileBytes)
        {
            throw new ApplicationFailureException("`.factory/Dockerfile` is larger than 64 KB.", nonRetryable: true);
        }

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var directory = new DirectoryInfo($"{basePath}/.factory/docker");
        if (directory.Exists && directory.LinkTarget is null)
        {
            long total = 0;
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(directory.FullName, file.FullName).Replace('\\', '/');
                if (file.LinkTarget is not null || HasSymlinkOnPath(directory.FullName, relative))
                {
                    throw new ApplicationFailureException($"`.factory/docker/{relative}` is or goes through a symbolic link, which is not allowed.", nonRetryable: true);
                }
                total += file.Length;
                if (files.Count >= MaxImageContextFiles || total > MaxImageContextBytes)
                {
                    throw new ApplicationFailureException("`.factory/docker/` has more than 50 files or more than 1 MB.", nonRetryable: true);
                }
                files[relative] = Convert.ToBase64String(File.ReadAllBytes(file.FullName));
            }
        }
        return new RepoImageSource(File.ReadAllText(dockerfile.FullName), files);
    }

    private static void RequireName(string name)
    {
        if (!WorkspaceName().IsMatch(name) || name.Contains(".."))
        {
            throw new ApplicationFailureException("Workspace name is invalid.", nonRetryable: true);
        }
    }

    private static bool IsPlainRelative(string path) =>
        path.Length is > 0 and <= 500 && !path.StartsWith('/') && !path.Contains('\0')
        && !path.Split('/').Any(part => part is "" or "." or "..");

    private static string Printable(string value) =>
        new(value.Where(c => !char.IsControl(c)).Take(80).ToArray());

    /// <summary>True if the file, or any directory above it inside <paramref name="root"/>, is a symbolic link.</summary>
    private static bool HasSymlinkOnPath(string root, string relative)
    {
        var current = root;
        foreach (var part in relative.Split('/'))
        {
            current = $"{current}/{part}";
            if (new FileInfo(current).LinkTarget is not null)
            {
                return true;
            }
        }
        return false;
    }

    private string RequireRoot()
    {
        var root = settings.WorkspaceRoot.TrimEnd('/');
        if (!root.StartsWith('/') || root.Length < 2)
        {
            throw new ApplicationFailureException(
                "FACTORY_WORKSPACE_ROOT is not configured for the controller.", nonRetryable: true);
        }
        return root;
    }

    private static void DeleteDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null)
        {
            directory.Delete(); // remove the link itself, never what it points to
        }
        else if (directory.Exists)
        {
            directory.Delete(recursive: true);
        }
    }
}
