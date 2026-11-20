using System.Text;
using Temporalio.Exceptions;

namespace DevFoundry.Controller;

public sealed partial class GitWorkspace
{
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
            inspection.Entries.SelectMany(entry => entry.Problems).Concat(inspection.Global).ToList())
        {
            Oversized = inspection.Entries.Where(entry => entry.Listed && entry.Oversized && entry.Kind == FileChange.Added).Select(entry => entry.Path).ToList(),
        };
    }

    /// <summary>
    /// The paths the default branch changed between an earlier commit and the one the clone is at now, read in the trusted clone, or null when that cannot be said (the commit is not
    /// there, or there are too many files). Used to see whether new commits touched anything an earlier run's saved results depend on.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ChangedSinceAsync(string name, string fromCommit, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        RequireName(name);
        if (!System.Text.RegularExpressions.Regex.IsMatch(fromCommit, "^[0-9a-f]{40}$"))
        {
            return null;
        }
        var basePath = BasePath(root, name);
        if (!Directory.Exists($"{basePath}/.git"))
        {
            return null;
        }
        try
        {
            var start = File.Exists($"{basePath}/.git/factory-start") ? File.ReadAllText($"{basePath}/.git/factory-start").Trim() : "HEAD";
            var output = await GitAsync(["diff", "--name-only", "-z", "--no-renames", fromCommit, start], basePath, useToken: false, cancellationToken);
            var paths = output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
            return paths.Count is 0 or > 500 || paths.Any(path => !IsPlainRelative(path)) ? null : paths;
        }
        catch (ApplicationFailureException)
        {
            return null; // the earlier commit is not in the clone (history was rewritten, or it is too old)
        }
    }

    /// <summary>
    /// Removes new files from the agent's copy that are over the scan limit (a log or recording a tool left behind), so they do not cost the agent an attempt.
    /// It checks every path itself (plain, not protected, no link on the way, not a file the repository already has, still a regular file over the limit), so nothing else can be deleted through it. Returns the paths removed.
    /// </summary>
    public IReadOnlyList<string> RemoveOversized(RemoveOversizedRequest request)
    {
        var root = RequireRoot();
        RequireName(request.Name);
        var work = WorkPath(root, request.Name);
        var basePath = BasePath(root, request.Name);
        var protectedPaths = ReadProtectedPaths(basePath);
        var removed = new List<string>();
        foreach (var path in request.Paths.Take(MaxChangedFiles))
        {
            // A file the repository already has (it exists in the pristine clone) is never removed here, only what the agent added.
            if (!IsPlainRelative(path) || ProtectedPaths.IsProtected(path, protectedPaths) || HasSymlinkOnPath(work, path) || File.Exists($"{basePath}/{path}"))
            {
                continue;
            }
            var info = new FileInfo($"{work}/{path}");
            if (info.Exists && info.LinkTarget is null && info.Length > SecretScan.MaxScanBytes)
            {
                info.Delete();
                removed.Add(path);
            }
        }
        return removed;
    }

    private async Task ResetBaseAsync(string basePath, CancellationToken cancellationToken)
    {
        var recorded = File.Exists($"{basePath}/.git/factory-start") ? File.ReadAllText($"{basePath}/.git/factory-start").Trim() : "";
        var start = System.Text.RegularExpressions.Regex.IsMatch(recorded, "^[0-9a-f]{40}$")
            ? recorded
            : (await GitAsync(["rev-parse", "origin/HEAD"], basePath, useToken: false, cancellationToken)).Trim();
        await GitAsync(["reset", "--hard", "--quiet", start], basePath, useToken: false, cancellationToken);
        await GitAsync(["clean", "--force", "-d", "-x", "--quiet"], basePath, useToken: false, cancellationToken);
    }

    /// <summary>One changed path and everything wrong with it. <see cref="Listed"/> files are part of the change report; <see cref="Oversized"/> ones are over the size or scan limit.</summary>
    private sealed record Entry(string Path, string Kind, bool Listed, string Reason, List<string> Problems, bool Oversized = false);

    private sealed record Inspection(List<Entry> Entries, List<string> Global, int ListedCount, long TotalBytes);

    private const string ProtectedFile = ".git/factory-protected";

    /// <summary>Remembers a project's extra protected paths in the trusted checkout (inside <c>.git</c>, which no agent can reach), one pattern per line.</summary>
    public void SetProtectedPaths(string name, IReadOnlyList<string> patterns)
    {
        var root = RequireRoot();
        RequireName(name);
        var basePath = BasePath(root, name);
        if (Directory.Exists($"{basePath}/.git"))
        {
            File.WriteAllLines($"{basePath}/{ProtectedFile}", patterns.Where(Globs.IsValidPattern));
        }
    }

    private static List<string> ReadProtectedPaths(string basePath) =>
        File.Exists($"{basePath}/{ProtectedFile}") ? File.ReadAllLines($"{basePath}/{ProtectedFile}").Where(Globs.IsValidPattern).ToList() : [];

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
        var knownSecrets = settings.HostTokens.ToArray();

        var extraProtected = ReadProtectedPaths(basePath);

        foreach (var raw in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 4)
            {
                continue;
            }
            var code = raw[..2];
            var path = raw[3..];
            var kind = code.Contains('D') ? FileChange.Deleted : code.Contains('?') || code.Contains('A') ? FileChange.Added : FileChange.Modified;
            entries.Add(InspectEntry(work, path, kind, extraProtected, knownSecrets, ref total));
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
    /// What is wrong, if anything, with one changed path: an unusable or protected path, a link, a file that is not a regular
    /// file, one that is too large, or one that holds a secret. Adds the size of a readable file to <paramref name="total"/>.
    /// </summary>
    private static Entry InspectEntry(
        string work, string path, string kind, IReadOnlyList<string> extraProtected, string[] knownSecrets, ref long total)
    {
        if (!IsPlainRelative(path))
        {
            return new Entry(Printable(path), kind, false, "unusable path", [$"unusable path in the changes: {Printable(path)}"]);
        }
        if (ProtectedPaths.IsProtected(path, extraProtected))
        {
            return new Entry(path, kind, false, "protected file", [$"{path} is a protected file that only a person may change"]);
        }
        var entry = new Entry(path, kind, true, "", []);
        if (kind == FileChange.Deleted)
        {
            return entry;
        }

        if (HasSymlinkOnPath(work, path))
        {
            entry.Problems.Add($"{path} is or goes through a symbolic link, which is not allowed");
            return entry with { Reason = "symbolic link" };
        }
        var info = new FileInfo($"{work}/{path}");
        if (!info.Exists)
        {
            entry.Problems.Add($"{path} is not a regular file");
            return entry with { Reason = "not a regular file" };
        }
        total += info.Length;
        if (info.Length > MaxFileBytes)
        {
            entry.Problems.Add($"{path} is {info.Length / 1024} KiB, more than the {MaxFileBytes / 1024} KiB limit per file");
            return entry with { Reason = $"larger than {MaxFileBytes / 1024 / 1024} MiB", Oversized = true };
        }
        if (info.Length > SecretScan.MaxScanBytes)
        {
            entry.Problems.Add(SecretScan.OversizeMessage(path));
            return entry with { Reason = $"larger than {SecretScan.MaxScanBytes / 1024 / 1024} MiB", Oversized = true };
        }
        var secrets = SecretScan.Scan(path, File.ReadAllBytes(info.FullName), knownSecrets).ToList();
        if (secrets.Count > 0)
        {
            entry.Problems.AddRange(secrets);
            return entry with { Reason = "possible secret" };
        }
        return entry;
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
}
