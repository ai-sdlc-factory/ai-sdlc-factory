namespace DevFoundry.Controller;

/// <summary>
/// Everything the factory asks of the git host that holds a repository's tickets and pull requests: the port the rest of the controller depends on. There is one implementation per kind of
/// host (<see cref="ForgejoClient"/>, <see cref="GitHubClient"/>); another host (GitLab, ...) would be another class behind the same operations. A host only carries events in and results out: it never decides
/// anything, and nothing here returns a title or a text that is not meant for a person or an agent to read as data.
/// </summary>
public interface IGitHost : IJanitorGitHost
{
    // ---- comments and tickets

    /// <summary>Posts a comment on a ticket or a pull request.</summary>
    Task PostIssueCommentAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken);

    /// <summary>The bodies of a ticket's latest comments, oldest first.</summary>
    Task<IReadOnlyList<string>> RecentCommentBodiesAsync(string owner, string repo, int issueNumber, int count, CancellationToken cancellationToken);

    /// <summary>Posts a notice unless one of the ticket's latest comments is already the same kind of notice (a string of comments gets one reply). Returns whether it posted.</summary>
    Task<bool> PostNoticeOnceAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken);

    /// <summary>Posts a notice unless a comment with exactly this text is anywhere in the ticket's last 200 comments. Returns whether it posted.</summary>
    Task<bool> PostNoticeUniqueAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken);

    /// <summary>Opens a ticket with these labels (by name). A label that does not exist fails the call.</summary>
    Task<IssueInfo> CreateIssueAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labelNames, CancellationToken cancellationToken);

    // ---- pull requests

    /// <summary>What the factory needs to know about a pull request before it touches the branch.</summary>
    Task<PullRequestDetails> GetPullRequestAsync(string owner, string repo, int number, CancellationToken cancellationToken);

    /// <summary>The files a pull request changes and the start of its diff.</summary>
    Task<PullChanges> GetPullChangesAsync(string owner, string repo, int number, CancellationToken cancellationToken);

    /// <summary>Every review still waiting to be acted on: by a person (not the bot), not dismissed, submitted after <paramref name="since"/>, with comments in resolved conversations left out.</summary>
    Task<IReadOnlyList<ReviewDetails>> GetUnaddressedReviewsAsync(string owner, string repo, int number, string botLogin, DateTimeOffset? since, CancellationToken cancellationToken);

    /// <summary>How many revisions the factory has answered on a pull request, and when the last reply was posted.</summary>
    Task<RevisionHistory> GetRevisionHistoryAsync(string owner, string repo, int number, CancellationToken cancellationToken);

    /// <summary>Asks people to look at a pull request again. Best effort: false when the host refused.</summary>
    Task<bool> RequestReviewAsync(string owner, string repo, int number, IReadOnlyList<string> reviewers, CancellationToken cancellationToken);

    /// <summary>The number of the open pull request the factory made for a ticket, or null.</summary>
    Task<int?> FindFactoryPullRequestAsync(string owner, string repo, int ticket, CancellationToken cancellationToken);

    /// <summary>Every pull request of a repository, open or not, as numbers, states and dates (for the weekly report).</summary>
    Task<IReadOnlyList<PullSummary>> ListAllPullRequestsAsync(string owner, string repo, CancellationToken cancellationToken);

    /// <summary>Opens a pull request from a pushed branch; when one already exists for the branch (a retried attempt) that one is returned.</summary>
    Task<PullRequestInfo> CreatePullRequestAsync(string owner, string repo, string head, string baseBranch, string title, string body, CancellationToken cancellationToken);

    /// <summary>
    /// What a person may do on a repository (<see cref="HostPermission.Write"/> includes a maintainer), by the host's own answer. A person the host does not know has none; a host that cannot say
    /// (the token may not look at other people's access) answers <see cref="HostPermission.Unknown"/>. Only a failure of the host itself is an exception.
    /// </summary>
    Task<HostPermission> GetPermissionAsync(string owner, string repo, string login, CancellationToken cancellationToken);

    // The janitor's five (repositories the bot can push to, branches, whether a ticket is closed, branches of open pull requests, deleting a branch) come from IJanitorGitHost.
}
