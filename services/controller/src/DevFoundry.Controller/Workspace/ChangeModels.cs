
namespace DevFoundry.Controller;

public sealed record FileChange(string Path, string Kind)
{
    public const string Added = "added";
    public const string Modified = "modified";
    public const string Deleted = "deleted";
}

/// <summary>What the agent changed in its workspace, and everything that stops those changes from being published.</summary>
public sealed record ChangeReport(IReadOnlyList<FileChange> Files, IReadOnlyList<string> Violations);

/// <summary><see cref="UpdateExistingBranch"/> pushes the commit onto the branch the checkout started from (a pull request being revised), never forcing and never choosing a new name.</summary>
public sealed record PublishRequest(string Name, string Message, RunRecordSpec? Record = null, bool UpdateExistingBranch = false);

public sealed record PublishResult(string CommitSha, string Branch, int FileCount);

public sealed record RescueRequest(string Name, string Message, RunRecordSpec? Record = null);

/// <summary>
/// What a rescue did with a run that could not finish: whether the work so far was saved to a branch, what was
/// left out and why, and where the full record of the run can be read.
/// </summary>
public sealed record RescueResult(
    bool Saved, string? Branch, int FileCount, IReadOnlyList<string> Excluded, string Note, string TraceUrl);
