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
        var inspection = await InspectAsync(name, cancellationToken);
        return new ChangeReport(
            inspection.Entries.Where(entry => entry.Listed).Select(entry => new FileChange(entry.Path, entry.Kind)).ToList(),
            inspection.Entries.SelectMany(entry => entry.Problems).Concat(inspection.Global).ToList());
    }

    private async Task ResetBaseAsync(string basePath, CancellationToken cancellationToken)
    {
        var start = (await GitAsync(["rev-parse", "origin/HEAD"], basePath, useToken: false, cancellationToken)).Trim();
        await GitAsync(["reset", "--hard", "--quiet", start], basePath, useToken: false, cancellationToken);
        await GitAsync(["clean", "--force", "-d", "-x", "--quiet"], basePath, useToken: false, cancellationToken);
    }

    /// <summary>One changed path and everything wrong with it. <see cref="Listed"/> files are part of the change report.</summary>
    private sealed record Entry(string Path, string Kind, bool Listed, string Reason, List<string> Problems);

    private sealed record Inspection(List<Entry> Entries, List<string> Global, int ListedCount, long TotalBytes);

    private async Task<Inspection> InspectAsync(string name, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        RequireName(name);
        var work = WorkPath(root, name);
        var basePath = BasePath(root, name);
        if (!Directory.Exists(work) || !Directory.Exists(basePath))
        {
            throw new ApplicationFailureException($"Workspace '{name}' does not exist.", nonRetryable: true);
        }

        // The pristine clone must stand for "the repository as the run found it". An earlier attempt to publish or
        // save may have committed to it, and then the agent's files would look unchanged (and a retry would conclude
        // there is nothing to do), so put it back first.
        await ResetBaseAsync(basePath, cancellationToken);

        var status = await GitAsync(
            [$"--git-dir={basePath}/.git", $"--work-tree={work}", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null",
             "status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames"],
            basePath, useToken: false, cancellationToken);

        var entries = new List<Entry>();
        var global = new List<string>();
        long total = 0;
        var knownSecrets = string.IsNullOrWhiteSpace(settings.ForgejoToken) ? [] : new[] { settings.ForgejoToken };

        foreach (var raw in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 4)
            {
                continue;
            }
            var code = raw[..2];
            var path = raw[3..];
            var kind = code.Contains('D') ? FileChange.Deleted : code.Contains('?') || code.Contains('A') ? FileChange.Added : FileChange.Modified;

            if (!IsPlainRelative(path))
            {
                entries.Add(new Entry(Printable(path), kind, false, "unusable path", [$"unusable path in the changes: {Printable(path)}"]));
                continue;
            }
            if (ProtectedPaths.IsProtected(path))
            {
                entries.Add(new Entry(path, kind, false, "protected file", [$"{path} is a protected file that only a person may change"]));
                continue;
            }
            var entry = new Entry(path, kind, true, "", []);
            entries.Add(entry);
            if (kind == FileChange.Deleted)
            {
                continue;
            }

            if (HasSymlinkOnPath(work, path))
            {
                entry.Problems.Add($"{path} is or goes through a symbolic link, which is not allowed");
                entries[^1] = entry with { Reason = "symbolic link" };
                continue;
            }
            var info = new FileInfo($"{work}/{path}");
            if (!info.Exists)
            {
                entry.Problems.Add($"{path} is not a regular file");
                entries[^1] = entry with { Reason = "not a regular file" };
                continue;
            }
            total += info.Length;
            if (info.Length > MaxFileBytes)
            {
                entry.Problems.Add($"{path} is {info.Length / 1024} KiB, more than the {MaxFileBytes / 1024} KiB limit per file");
                entries[^1] = entry with { Reason = $"larger than {MaxFileBytes / 1024 / 1024} MiB" };
                continue;
            }
            var secrets = SecretScan.Scan(path, File.ReadAllBytes(info.FullName), knownSecrets).ToList();
            if (secrets.Count > 0)
            {
                entry.Problems.AddRange(secrets);
                entries[^1] = entry with { Reason = "possible secret" };
            }
        }

        var listed = entries.Count(entry => entry.Listed);
        if (listed > MaxChangedFiles)
        {
            global.Add($"{listed} files changed, more than the limit of {MaxChangedFiles}; build output and caches belong in .gitignore");
        }
        if (total > MaxTotalBytes)
        {
            global.Add($"the changes add up to {total / 1024 / 1024} MiB, more than the {MaxTotalBytes / 1024 / 1024} MiB limit");
        }
        return new Inspection(entries, global, listed, total);
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
            if (info.Exists && info.LinkTarget is null && info.Length > 64 * 1024)
            {
                // Too big to show, but say how long it is so a reviewer (and the size rules) can tell.
                var lineCount = File.ReadLines(info.FullName).Count();
                text.AppendLine($"diff --git a/{added.Path} b/{added.Path}\nnew file (large: {lineCount} lines, not shown)");
                continue;
            }
            if (!info.Exists || info.LinkTarget is not null)
            {
                text.AppendLine($"diff --git a/{added.Path} b/{added.Path}\nnew file (not a regular file, not shown)");
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
        return text.Length > UnifiedDiff.MaxChars ? text.ToString(0, UnifiedDiff.MaxChars) : text.ToString();
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

        ApplyChanges(work, basePath, report.Files);
        var (sha, branch) = await CommitAndPushAsync(basePath, request.Message, suffix: "", cancellationToken);
        return new PublishResult(sha, branch, report.Files.Count);
    }

    /// <summary>
    /// Saves what a run that could not finish had written so far to a work-in-progress branch, so a person can pick
    /// it up. It goes through the same trusted path as a publish (files are copied onto the pristine clone), but the
    /// checks do not have to pass: only files with a problem of their own (protected, a symbolic link, too large, a
    /// possible secret) are left out and reported. Nothing is saved if there are too many files or too much data
    /// (usually build output), or if nothing changed.
    /// </summary>
    public async Task<RescueResult> RescueAsync(RescueRequest request, string traceUrl, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        RequireName(request.Name);
        var work = WorkPath(root, request.Name);
        var basePath = BasePath(root, request.Name);

        var inspection = await InspectAsync(request.Name, cancellationToken);
        var excluded = inspection.Entries
            .Where(entry => entry.Problems.Count > 0)
            .Select(entry => $"{entry.Path}: {entry.Reason}")
            .ToList();
        var saveable = inspection.Entries.Where(entry => entry.Listed && entry.Problems.Count == 0)
            .Select(entry => new FileChange(entry.Path, entry.Kind)).ToList();

        if (inspection.Global.Count > 0)
        {
            return new RescueResult(false, null, 0, excluded,
                "too much changed to save: " + string.Join("; ", inspection.Global), traceUrl);
        }
        if (saveable.Count == 0)
        {
            return new RescueResult(false, null, 0, excluded,
                excluded.Count > 0 ? "every changed file had a problem of its own" : "no files had changed", traceUrl);
        }

        ApplyChanges(work, basePath, saveable);
        var (_, branch) = await CommitAndPushAsync(basePath, request.Message, suffix: "-wip", cancellationToken);
        return new RescueResult(true, branch, saveable.Count, excluded, "", traceUrl);
    }

    /// <summary>Copies the verified files from the agent's tree onto the pristine clone (regular files only).</summary>
    private static void ApplyChanges(string work, string basePath, IEnumerable<FileChange> changes)
    {
        foreach (var change in changes)
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
    }

    /// <summary>
    /// Commits (unless an earlier attempt already did), picks a branch name that is free on the remote, and pushes.
    /// Safe to repeat: an attempt that finds its own commit already on the remote reuses that branch.
    /// </summary>
    private async Task<(string Sha, string Branch)> CommitAndPushAsync(
        string basePath, string message, string suffix, CancellationToken cancellationToken)
    {
        await GitAsync(["add", "--all"], basePath, useToken: false, cancellationToken);
        if ((await GitAsync(["status", "--porcelain"], basePath, useToken: false, cancellationToken)).Trim().Length > 0)
        {
            await GitAsync(["-c", "core.hooksPath=/dev/null", "commit", "--quiet", "-m", message], basePath, useToken: false, cancellationToken);
        }

        var sha = (await GitAsync(["rev-parse", "HEAD"], basePath, useToken: false, cancellationToken)).Trim();
        var current = (await GitAsync(["rev-parse", "--abbrev-ref", "HEAD"], basePath, useToken: false, cancellationToken)).Trim();
        var (branch, alreadyThere) = await ChooseBranchAsync(basePath, StripSuffix(current, suffix) + suffix, cancellationToken);
        if (branch != current)
        {
            await GitAsync(["branch", "--move", branch], basePath, useToken: false, cancellationToken);
        }
        if (!alreadyThere)
        {
            await GitAsync(["-c", "core.hooksPath=/dev/null", "push", "--quiet", "origin", $"HEAD:refs/heads/{branch}"], basePath, useToken: true, cancellationToken);
        }
        return (sha, branch);
    }

    private static string StripSuffix(string name, string suffix) =>
        suffix.Length > 0 && name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name;

    /// <summary>
    /// The first of <c>name</c>, <c>name-2</c>, <c>name-3</c> ... that is free on the remote, so a re-run on the same
    /// ticket never collides with, or overwrites, an earlier branch. A branch that already holds exactly the content
    /// about to be pushed (the earlier attempt of this same run got that far) is reused, not duplicated.
    /// </summary>
    private async Task<(string Name, bool AlreadyThere)> ChooseBranchAsync(string basePath, string desired, CancellationToken cancellationToken)
    {
        var ourTree = (await GitAsync(["rev-parse", "HEAD^{tree}"], basePath, useToken: false, cancellationToken)).Trim();
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var candidate = attempt == 1 ? desired : $"{desired}-{attempt}";
            var listing = (await GitAsync(["ls-remote", "--heads", "origin", $"refs/heads/{candidate}"], basePath, useToken: true, cancellationToken)).Trim();
            if (listing.Length == 0)
            {
                return (candidate, false);
            }
            await GitAsync(["fetch", "--quiet", "origin", $"refs/heads/{candidate}"], basePath, useToken: true, cancellationToken);
            var theirTree = (await GitAsync(["rev-parse", "FETCH_HEAD^{tree}"], basePath, useToken: false, cancellationToken)).Trim();
            if (theirTree == ourTree)
            {
                return (candidate, true);
            }
        }
        throw new ApplicationFailureException($"Too many branches named {desired} already exist.", nonRetryable: true);
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
