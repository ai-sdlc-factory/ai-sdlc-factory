using Temporalio.Exceptions;

namespace DevFoundry.Controller;

public sealed partial class GitWorkspace
{
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
        AddRunRecord(root, request.Name, basePath, request.Record);
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
        AddRunRecord(root, request.Name, basePath, request.Record);
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
}
