using System.Text.RegularExpressions;
using Temporalio.Exceptions;

namespace DevFoundry.Controller;

public sealed partial class GitWorkspace
{
    // Both live inside .git of the pristine clone, where no agent can reach them (the agent's copy gets a copy of them, which changes nothing here).
    private const string MergeOriginFile = ".git/factory-merge-origin";
    private const string ConflictsFile = ".git/factory-conflicts";

    /// <summary>The most files a merge may leave in conflict for the factory to try; more than this is a job for a person.</summary>
    public const int MaxConflictedFiles = 40;

    /// <summary>A line git writes at the start or the end of a conflicted section: seven angle brackets, then a space and a name (or nothing).</summary>
    [GeneratedRegex(@"^(<{7}|>{7})( |\r?$)", RegexOptions.Multiline)]
    private static partial Regex ConflictMarker();

    /// <summary>
    /// Merges the base branch into the pull request's branch, in the pristine clone (git runs only there, never in the agent's copy), and hands the
    /// result to the agent's copy. The merge is committed locally even when it has conflicts: that commit, with the conflict markers still in the
    /// files, becomes the point every later inspection compares with, so the changes the agent makes are exactly its resolutions and fixes, and the
    /// files the merge combined by itself are not mistaken for the agent's work. The commit is never pushed; <see cref="PublishMergeAsync"/> pushes
    /// a new merge commit with the same two parents. Safe to repeat: it always starts again from the branch's tip.
    /// </summary>
    public async Task<MergeResult> MergeBaseAsync(MergeBaseRequest request, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        RequireName(request.Name);
        if (!BranchName().IsMatch(request.BaseBranch) || request.BaseBranch.Contains(".."))
        {
            throw new ApplicationFailureException("The base branch has an invalid name.", nonRetryable: true);
        }
        var work = WorkPath(root, request.Name);
        var basePath = BasePath(root, request.Name);
        if (!Directory.Exists(work) || !Directory.Exists(basePath))
        {
            throw new ApplicationFailureException($"Workspace '{request.Name}' does not exist.", nonRetryable: true);
        }

        // The branch's tip, remembered the first time so that a repeated attempt (which would otherwise start from its own earlier merge) starts again from it.
        var originFile = $"{basePath}/{MergeOriginFile}";
        string origin;
        if (File.Exists(originFile) && Regex.IsMatch(File.ReadAllText(originFile).Trim(), "^[0-9a-f]{40}$"))
        {
            origin = File.ReadAllText(originFile).Trim();
        }
        else
        {
            origin = (await GitAsync(["rev-parse", "HEAD"], basePath, useToken: false, cancellationToken)).Trim();
            File.WriteAllText(originFile, origin);
        }
        await GitAsync(["merge", "--abort"], basePath, useToken: false, cancellationToken, allowFailure: true);
        await GitAsync(["reset", "--hard", "--quiet", origin], basePath, useToken: false, cancellationToken);
        await GitAsync(["clean", "--force", "-d", "-x", "--quiet"], basePath, useToken: false, cancellationToken);
        File.WriteAllText($"{basePath}/.git/factory-start", origin);
        File.Delete($"{basePath}/{ConflictsFile}");

        await GitAsync(["fetch", "--quiet", "origin", $"refs/heads/{request.BaseBranch}"], basePath, useToken: true, cancellationToken);
        var baseCommit = (await GitAsync(["rev-parse", "FETCH_HEAD^{commit}"], basePath, useToken: false, cancellationToken)).Trim();
        var missing = int.Parse((await GitAsync(["rev-list", "--count", $"HEAD..{baseCommit}"], basePath, useToken: false, cancellationToken)).Trim());
        if (missing == 0)
        {
            return new MergeResult(MergeResult.UpToDate, baseCommit, []);
        }

        var branch = (await GitAsync(["rev-parse", "--abbrev-ref", "HEAD"], basePath, useToken: false, cancellationToken)).Trim();
        try
        {
            await GitAsync(["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "merge", "--no-ff", "--no-commit", "--quiet", baseCommit], basePath, useToken: false, cancellationToken);
        }
        catch (ApplicationFailureException error)
        {
            // A merge with conflicts exits with an error but is in progress; anything else (unrelated histories, a file in the way) is not.
            if (!File.Exists($"{basePath}/.git/MERGE_HEAD"))
            {
                throw new ApplicationFailureException($"Git could not merge {request.BaseBranch} into {branch}: {error.Message}", nonRetryable: true);
            }
        }

        var conflicts = await ReadConflictsAsync(basePath, cancellationToken);
        await GitAsync(["add", "--all"], basePath, useToken: false, cancellationToken);
        File.WriteAllLines($"{basePath}/{ConflictsFile}", conflicts.Select(conflict => conflict.Path));
        await GitAsync(["-c", "core.hooksPath=/dev/null", "commit", "--quiet", "--no-verify", "-m", $"Merge branch '{request.BaseBranch}' into {branch}"], basePath, useToken: false, cancellationToken);
        var merge = (await GitAsync(["rev-parse", "HEAD"], basePath, useToken: false, cancellationToken)).Trim();
        File.WriteAllText($"{basePath}/.git/factory-start", merge);
        var brought = (await GitAsync(["diff", "--name-only", origin, merge], basePath, useToken: false, cancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        // The agent works on a fresh copy of the merged tree (it has not run yet, so nothing of its is lost).
        DeleteDirectory(work);
        Directory.CreateDirectory(work);
        await RunAsync("cp", ["-a", $"{basePath}/.", work], root, cancellationToken);
        return new MergeResult(conflicts.Count > 0 ? MergeResult.Conflicted : MergeResult.Merged, baseCommit, conflicts, brought);
    }

    /// <summary>The files a merge in progress could not combine, with what each side did to them (git's "us" is the pull request's branch, "them" the base).</summary>
    private async Task<List<ConflictedFile>> ReadConflictsAsync(string basePath, CancellationToken cancellationToken)
    {
        var status = await GitAsync(["status", "--porcelain=v1", "-z", "--no-renames", "--untracked-files=no"], basePath, useToken: false, cancellationToken);
        var conflicts = new List<ConflictedFile>();
        foreach (var raw in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 4)
            {
                continue;
            }
            var kind = raw[..2] switch
            {
                "UU" => "both changed it",
                "AA" => "both added it",
                "DU" => "the pull request deleted it, the base changed it",
                "UD" => "the base deleted it, the pull request changed it",
                "AU" => "the pull request added it, the base has a different one",
                "UA" => "the base added it, the pull request has a different one",
                "DD" => "both deleted it",
                _ => null,
            };
            if (kind is not null)
            {
                conflicts.Add(new ConflictedFile(raw[3..], kind));
            }
        }
        return conflicts.OrderBy(conflict => conflict.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The files that were in conflict and still hold conflict markers in the agent's copy. A conflict is not resolved while a marker is left,
    /// whatever the checks say (some languages would even build with one in a comment or a string).
    /// </summary>
    public Task<IReadOnlyList<string>> FindConflictMarkersAsync(string name)
    {
        var root = RequireRoot();
        RequireName(name);
        var work = WorkPath(root, name);
        var list = $"{BasePath(root, name)}/{ConflictsFile}";
        var found = new List<string>();
        if (File.Exists(list))
        {
            foreach (var path in File.ReadAllLines(list).Where(IsPlainRelative))
            {
                var info = new FileInfo($"{work}/{path}");
                if (!info.Exists || info.LinkTarget is not null || HasSymlinkOnPath(work, path) || info.Length > MaxFileBytes)
                {
                    continue; // deleted, or a problem the change check reports on its own
                }
                if (ConflictMarker().IsMatch(File.ReadAllText(info.FullName)))
                {
                    found.Add(path);
                }
            }
        }
        return Task.FromResult<IReadOnlyList<string>>(found);
    }

    /// <summary>
    /// Pushes the merge as one commit with two parents, the branch's tip and the base's tip: the files the agent changed (its resolutions and any fix)
    /// are applied onto the merge in the pristine clone, so the commit holds exactly what was checked. Never forced: if a person pushed to the branch
    /// meanwhile, nothing is pushed.
    /// </summary>
    public async Task<PublishResult> PublishMergeAsync(PublishMergeRequest request, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        RequireName(request.Name);
        var work = WorkPath(root, request.Name);
        var basePath = BasePath(root, request.Name);

        var report = await DetectChangesAsync(request.Name, cancellationToken);
        if (report.Violations.Count > 0)
        {
            throw new ApplicationFailureException("Refusing to publish: " + string.Join("; ", report.Violations.Take(5)), nonRetryable: true);
        }
        if ((await FindConflictMarkersAsync(request.Name)) is { Count: > 0 } unresolved)
        {
            throw new ApplicationFailureException($"Refusing to publish: conflict markers are still in {string.Join(", ", unresolved.Take(5))}.", nonRetryable: true);
        }

        ApplyChanges(work, basePath, report.Files);
        AddRunRecord(root, request.Name, basePath, request.Record);
        await GitAsync(["add", "--all"], basePath, useToken: false, cancellationToken);
        var tree = (await GitAsync(["write-tree"], basePath, useToken: false, cancellationToken)).Trim();
        var parents = (await GitAsync(["rev-list", "--parents", "-n", "1", "HEAD"], basePath, useToken: false, cancellationToken)).Trim().Split(' ');
        if (parents.Length != 3)
        {
            throw new ApplicationFailureException("The checkout does not hold a merge, so there is nothing to publish.", nonRetryable: true);
        }
        var commit = (await GitAsync(["commit-tree", tree, "-p", parents[1], "-p", parents[2], "-m", request.Message], basePath, useToken: false, cancellationToken)).Trim();
        await GitAsync(["reset", "--hard", "--quiet", commit], basePath, useToken: false, cancellationToken);
        var branch = (await GitAsync(["rev-parse", "--abbrev-ref", "HEAD"], basePath, useToken: false, cancellationToken)).Trim();
        try
        {
            await GitAsync(["-c", "core.hooksPath=/dev/null", "push", "--quiet", "origin", $"HEAD:refs/heads/{branch}"], basePath, useToken: true, cancellationToken);
        }
        catch (ApplicationFailureException error) when (IsRejectedPush(error))
        {
            // Either a person pushed meanwhile, or an earlier attempt of this same publish got through before it was reported (its commit differs
            // only in the time it was made, so the same tree on the remote's tip is that attempt).
            await GitAsync(["fetch", "--quiet", "origin", $"refs/heads/{branch}"], basePath, useToken: true, cancellationToken);
            var theirTree = (await GitAsync(["rev-parse", "FETCH_HEAD^{tree}"], basePath, useToken: false, cancellationToken)).Trim();
            var theirParents = (await GitAsync(["rev-list", "--parents", "-n", "1", "FETCH_HEAD"], basePath, useToken: false, cancellationToken)).Trim().Split(' ');
            if (theirTree == tree && theirParents.Length == 3 && theirParents[1] == parents[1] && theirParents[2] == parents[2])
            {
                return new PublishResult(theirParents[0], branch, report.Files.Count);
            }
            throw new ApplicationFailureException($"The branch {branch} changed while the update was being made, so nothing was pushed. Comment `/update` again.", nonRetryable: true);
        }
        return new PublishResult(commit, branch, report.Files.Count);
    }
}
