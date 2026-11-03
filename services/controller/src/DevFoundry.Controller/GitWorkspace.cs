using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Temporalio.Exceptions;

namespace DevFoundry.Controller;

public sealed record WorkspaceRequest(string Owner, string Repo, string Name, string Branch);

public sealed record WorkspaceInfo(string Name, string Path, string Branch, string DefaultBranch, string HeadCommit);

/// <summary>
/// Creates and removes the per-run git checkouts that sandboxes mount. Only the controller does
/// this, because only it holds the Forgejo token; the token is handed to git through the
/// environment, so it appears in no argument list and is never written to the repository config.
/// </summary>
public sealed partial class GitWorkspace(WorkerSettings settings, Func<string, string, string>? remoteUrl = null)
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
        await GitAsync(["checkout", "--quiet", "-b", request.Branch], basePath, useToken: false, cancellationToken);
        await GitAsync(["config", "user.name", "Dev Foundry"], basePath, useToken: false, cancellationToken);
        await GitAsync(["config", "user.email", "factory-bot@dev-foundry.test"], basePath, useToken: false, cancellationToken);

        var head = (await GitAsync(["rev-parse", "HEAD"], basePath, useToken: false, cancellationToken)).Trim();
        var defaultBranch = (await GitAsync(["rev-parse", "--abbrev-ref", "origin/HEAD"], basePath, useToken: false, cancellationToken))
            .Trim().Replace("origin/", "", StringComparison.Ordinal);

        // The agent gets a copy (including .git, so it can use git itself); the pristine clone stays untouched.
        Directory.CreateDirectory(work);
        await RunAsync("cp", ["-a", $"{basePath}/.", work], root, cancellationToken);
        return new WorkspaceInfo(request.Name, work, request.Branch, defaultBranch, head);
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
    /// What changed in the agent's workspace compared with the pristine clone, and why it may not be
    /// published. Git reads the agent's tree as plain files (the git directory is the trusted one), so
    /// nothing inside the workspace can run code here, and .gitignore decides what counts as noise.
    /// </summary>
    public async Task<ChangeReport> DetectChangesAsync(string name, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        RequireName(name);
        var work = WorkPath(root, name);
        var basePath = BasePath(root, name);
        if (!Directory.Exists(work) || !Directory.Exists(basePath))
        {
            throw new ApplicationFailureException($"Workspace '{name}' does not exist.", nonRetryable: true);
        }

        var status = await GitAsync(
            [$"--git-dir={basePath}/.git", $"--work-tree={work}", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null",
             "status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames"],
            basePath, useToken: false, cancellationToken);

        var files = new List<FileChange>();
        var violations = new List<string>();
        long total = 0;
        var knownSecrets = string.IsNullOrWhiteSpace(settings.ForgejoToken) ? [] : new[] { settings.ForgejoToken };

        foreach (var entry in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Length < 4)
            {
                continue;
            }
            var code = entry[..2];
            var path = entry[3..];
            var kind = code.Contains('D') ? FileChange.Deleted : code.Contains('?') || code.Contains('A') ? FileChange.Added : FileChange.Modified;

            if (!IsPlainRelative(path))
            {
                violations.Add($"unusable path in the changes: {Printable(path)}");
                continue;
            }
            if (ProtectedPaths.IsProtected(path))
            {
                violations.Add($"{path} is a protected file that only a person may change");
                continue;
            }
            files.Add(new FileChange(path, kind));
            if (kind == FileChange.Deleted)
            {
                continue;
            }

            if (HasSymlinkOnPath(work, path))
            {
                violations.Add($"{path} is or goes through a symbolic link, which is not allowed");
                continue;
            }
            var info = new FileInfo($"{work}/{path}");
            if (!info.Exists)
            {
                violations.Add($"{path} is not a regular file");
                continue;
            }
            total += info.Length;
            if (info.Length > MaxFileBytes)
            {
                violations.Add($"{path} is {info.Length / 1024} KiB, more than the {MaxFileBytes / 1024} KiB limit per file");
                continue;
            }
            violations.AddRange(SecretScan.Scan(path, File.ReadAllBytes(info.FullName), knownSecrets));
        }

        if (files.Count > MaxChangedFiles)
        {
            violations.Add($"{files.Count} files changed, more than the limit of {MaxChangedFiles}; build output and caches belong in .gitignore");
        }
        if (total > MaxTotalBytes)
        {
            violations.Add($"the changes add up to {total / 1024 / 1024} MiB, more than the {MaxTotalBytes / 1024 / 1024} MiB limit");
        }
        return new ChangeReport(files, violations);
    }

    /// <summary>
    /// A unified diff of the agent's changes for the reviewers, produced by the controller so the reviewing
    /// agents need no shell. Tracked files come from git (trusted directory, agent tree read as plain files);
    /// new files are shown whole. Capped, because it goes into a prompt.
    /// </summary>
    public async Task<string> DiffAsync(string name, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        RequireName(name);
        var work = WorkPath(root, name);
        var basePath = BasePath(root, name);
        var report = await DetectChangesAsync(name, cancellationToken);
        var text = new StringBuilder();

        var tracked = report.Files.Where(file => file.Kind != FileChange.Added).Select(file => file.Path).ToList();
        if (tracked.Count > 0)
        {
            string[] arguments =
            [
                $"--git-dir={basePath}/.git", $"--work-tree={work}", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null",
                "diff", "--no-color", "--no-ext-diff", "--no-textconv", "-U3", "--", .. tracked,
            ];
            text.Append(await GitAsync(arguments, basePath, useToken: false, cancellationToken));
        }
        foreach (var added in report.Files.Where(file => file.Kind == FileChange.Added))
        {
            var info = new FileInfo($"{work}/{added.Path}");
            if (!info.Exists || info.LinkTarget is not null || info.Length > 64 * 1024)
            {
                text.AppendLine($"diff --git a/{added.Path} b/{added.Path}\nnew file (not shown: large or not a regular file)");
                continue;
            }
            var bytes = File.ReadAllBytes(info.FullName);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
            {
                text.AppendLine($"diff --git a/{added.Path} b/{added.Path}\nnew file (binary, not shown)");
                continue;
            }
            text.AppendLine($"diff --git a/{added.Path} b/{added.Path}\nnew file\n--- /dev/null\n+++ b/{added.Path}");
            foreach (var line in Encoding.UTF8.GetString(bytes).TrimEnd('\n').Split('\n'))
            {
                text.AppendLine("+" + line);
            }
        }
        return text.Length > 200_000 ? text.ToString(0, 200_000) : text.ToString();
    }

    /// <summary>
    /// Applies the verified changes onto the pristine clone, commits there, and pushes the branch.
    /// The checks run again here, so a publish never depends on an earlier report. Returns null when
    /// there is nothing to commit.
    /// </summary>
    public async Task<PublishResult?> PublishAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        RequireName(request.Name);
        var work = WorkPath(root, request.Name);
        var basePath = BasePath(root, request.Name);

        var report = await DetectChangesAsync(request.Name, cancellationToken);
        if (report.Violations.Count > 0)
        {
            throw new ApplicationFailureException(
                "Refusing to publish: " + string.Join("; ", report.Violations.Take(5)), nonRetryable: true);
        }
        if (report.Files.Count == 0)
        {
            return null;
        }

        foreach (var change in report.Files)
        {
            var target = $"{basePath}/{change.Path}";
            if (HasSymlinkOnPath(basePath, change.Path))
            {
                throw new ApplicationFailureException($"Refusing to publish: {change.Path} goes through a symbolic link.", nonRetryable: true);
            }
            if (change.Kind == FileChange.Deleted)
            {
                File.Delete(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy($"{work}/{change.Path}", target, overwrite: true);
            var executable = File.GetUnixFileMode($"{work}/{change.Path}").HasFlag(UnixFileMode.UserExecute);
            File.SetUnixFileMode(target, executable ? (UnixFileMode)0b111_101_101 : (UnixFileMode)0b110_100_100);
        }

        await GitAsync(["add", "--all"], basePath, useToken: false, cancellationToken);
        await GitAsync(["-c", "core.hooksPath=/dev/null", "commit", "--quiet", "-m", request.Message], basePath, useToken: false, cancellationToken);
        var branch = (await GitAsync(["rev-parse", "--abbrev-ref", "HEAD"], basePath, useToken: false, cancellationToken)).Trim();
        await GitAsync(["-c", "core.hooksPath=/dev/null", "push", "--quiet", "origin", $"HEAD:refs/heads/{branch}"], basePath, useToken: true, cancellationToken);
        var sha = (await GitAsync(["rev-parse", "HEAD"], basePath, useToken: false, cancellationToken)).Trim();
        return new PublishResult(sha, branch, report.Files.Count);
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

    private static async Task RunAsync(string program, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { WorkingDirectory = workingDirectory, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new ApplicationFailureException($"Could not start {program}.", nonRetryable: true);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new ApplicationFailureException($"{program} failed: {(await error).Trim()}");
        }
    }

    private async Task<string> GitAsync(
        IReadOnlyList<string> arguments, string workingDirectory, bool useToken, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        if (useToken && !string.IsNullOrWhiteSpace(settings.ForgejoToken))
        {
            start.Environment["GIT_CONFIG_COUNT"] = "1";
            start.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            start.Environment["GIT_CONFIG_VALUE_0"] = $"Authorization: token {settings.ForgejoToken}";
        }

        using var process = Process.Start(start)
            ?? throw new ApplicationFailureException("Could not start git.", nonRetryable: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            // The token is passed by environment, so it cannot appear in git's own messages.
            var message = (await error).Trim();
            throw new ApplicationFailureException(
                $"git {arguments[0]} failed: {(message.Length > 500 ? message[..500] : message)}",
                nonRetryable: message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("403") || message.Contains("401"));
        }
        return await output;
    }
}
