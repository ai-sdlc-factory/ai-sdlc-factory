namespace DevFoundry.Controller;

/// <summary>A pull request the host opened for the factory: its number and where a person finds it.</summary>
public sealed record PullRequestInfo(int Number, string Url);

/// <summary>A ticket the factory opened.</summary>
public sealed record IssueInfo(int Number, string Url);

/// <summary>A branch of a repository and when its last commit was made (for the janitor).</summary>
public sealed record BranchInfo(string Name, DateTimeOffset LastCommit);
