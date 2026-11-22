namespace AiSdlcFactory.Controller;

/// <summary>A pull request the host opened for the factory: its number and where a person finds it.</summary>
public sealed record PullRequestInfo(int Number, string Url);

/// <summary>A ticket the factory opened.</summary>
public sealed record IssueInfo(int Number, string Url);

/// <summary>What a person may do on a repository, as the host says. <see cref="Unknown"/> when it cannot say (the token may not be allowed to look at other people's access).</summary>
public enum HostPermission
{
    Unknown,
    None,
    Read,
    Write,
    Admin,
}

/// <summary>A branch of a repository and when its last commit was made (for the janitor).</summary>
public sealed record BranchInfo(string Name, DateTimeOffset LastCommit);
