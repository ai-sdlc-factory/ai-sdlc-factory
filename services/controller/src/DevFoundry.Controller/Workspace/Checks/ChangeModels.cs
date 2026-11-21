
namespace DevFoundry.Controller;

public sealed record FileChange(string Path, string Kind)
{
    public const string Added = "added";
    public const string Modified = "modified";
    public const string Deleted = "deleted";
}

/// <summary>What the agent changed in its workspace, and everything that stops those changes from being published.</summary>
public sealed record ChangeReport(IReadOnlyList<FileChange> Files, IReadOnlyList<string> Violations)
{
    /// <summary>New files (not changes to tracked ones) that are too large to publish or to scan, such as a log or a recording a tool left behind.</summary>
    public IReadOnlyList<string> Oversized { get; init; } = [];
}

/// <summary>Asks the controller to remove files that are too large; it removes only files that really are, so it cannot be used to delete anything else.</summary>
public sealed record RemoveOversizedRequest(string Name, IReadOnlyList<string> Paths);

/// <summary><see cref="UpdateExistingBranch"/> pushes the commit onto the branch the checkout started from (a pull request being revised), never forcing and never choosing a new name.</summary>
public sealed record PublishRequest(string Name, string Message, RunRecordSpec? Record = null, bool UpdateExistingBranch = false);

/// <summary>
/// The sandbox image a repository describes in <c>.factory/Dockerfile</c> and the small context next to it (<c>.factory/docker/</c>, paths relative
/// to that folder, contents base64). Read from the trusted checkout, so it is what the default branch holds, never what an agent wrote.
/// </summary>
public sealed record RepoImageSource(string Dockerfile, IReadOnlyDictionary<string, string> Files);

/// <summary>The settings file <c>/setup</c> proposes. The controller writes it itself (an agent cannot write under <c>.factory/</c>); only this one path is possible.</summary>
public sealed record PublishSettingsRequest(string Name, string Content, string Message);

public sealed record ProtectedPathsRequest(string Name, IReadOnlyList<string> Patterns);

public sealed record PublishResult(string CommitSha, string Branch, int FileCount, int RebasedOver = 0);

/// <summary>Merge the remote <see cref="BaseBranch"/> into the branch the checkout started from (an <c>/update</c>).</summary>
public sealed record MergeBaseRequest(string Name, string BaseBranch);

/// <summary>One file the merge could not combine by itself, and why (for the resolver, and for the comment on the pull request).</summary>
public sealed record ConflictedFile(string Path, string Kind);

/// <summary>What merging the base branch did. <see cref="Conflicts"/> is empty unless the outcome is <see cref="Conflicted"/>.</summary>
/// <param name="BaseCommit">The tip of the base branch that was merged.</param>
/// <param name="FilesBrought">How many files the merge changed compared with the pull request's branch.</param>
public sealed record MergeResult(string Outcome, string BaseCommit, IReadOnlyList<ConflictedFile> Conflicts, int FilesBrought = 0)
{
    /// <summary>The branch already has everything the base branch has; nothing to do.</summary>
    public const string UpToDate = "up_to_date";

    /// <summary>The merge went through without conflicts; the checkout holds the merged tree.</summary>
    public const string Merged = "merged";

    /// <summary>The merge stopped at conflicts; the checkout holds the files with conflict markers in them.</summary>
    public const string Conflicted = "conflicted";
}

/// <summary>Publish the merge of the base branch (and whatever the resolver changed on top of it) as one merge commit with both parents, never forcing.</summary>
public sealed record PublishMergeRequest(string Name, string Message, RunRecordSpec? Record = null);

public sealed record RescueRequest(string Name, string Message, RunRecordSpec? Record = null);

/// <summary>
/// What a rescue did with a run that could not finish: whether the work so far was saved to a branch, what was
/// left out and why, and where the full record of the run can be read.
/// </summary>
public sealed record RescueResult(
    bool Saved, string? Branch, int FileCount, IReadOnlyList<string> Excluded, string Note, string TraceUrl);
