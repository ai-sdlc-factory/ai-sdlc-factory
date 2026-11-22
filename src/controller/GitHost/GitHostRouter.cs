using Temporalio.Exceptions;

namespace AiSdlcFactory.Controller;

/// <summary>
/// The git host port when there is more than one host: each call goes to the host the owner belongs to (<c>acme</c> is the default host's, <c>work:acme</c> is the host named
/// <c>work</c>'s; see <see cref="RepoNames"/>), with the qualifier taken off, so no host ever sees another's name. Listing repositories asks every host and qualifies what comes back.
/// </summary>
public sealed class GitHostRouter(IReadOnlyDictionary<string, IGitHost> hosts) : IGitHost
{
    private (IGitHost Host, string Owner) For(string qualifiedOwner)
    {
        var (name, owner) = RepoNames.Split(qualifiedOwner);
        return hosts.TryGetValue(name, out var host)
            ? (host, owner)
            : throw new ApplicationFailureException($"No git host named \"{name}\" is configured (see FACTORY_HOSTS).", nonRetryable: true);
    }

    public Task<string?> ReadFileAsync(string owner, string repo, string path, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.ReadFileAsync(plain, repo, path, cancellationToken);
    }

    public Task<HostPermission> GetPermissionAsync(string owner, string repo, string login, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.GetPermissionAsync(plain, repo, login, cancellationToken);
    }

    public Task PostIssueCommentAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.PostIssueCommentAsync(plain, repo, issueNumber, body, cancellationToken);
    }

    public Task<IReadOnlyList<string>> RecentCommentBodiesAsync(string owner, string repo, int issueNumber, int count, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.RecentCommentBodiesAsync(plain, repo, issueNumber, count, cancellationToken);
    }

    public Task<bool> PostNoticeOnceAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.PostNoticeOnceAsync(plain, repo, issueNumber, body, cancellationToken);
    }

    public Task<bool> PostNoticeUniqueAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.PostNoticeUniqueAsync(plain, repo, issueNumber, body, cancellationToken);
    }

    public Task<IssueInfo> CreateIssueAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labelNames, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.CreateIssueAsync(plain, repo, title, body, labelNames, cancellationToken);
    }

    public Task<PullRequestDetails> GetPullRequestAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.GetPullRequestAsync(plain, repo, number, cancellationToken);
    }

    public Task<PullChanges> GetPullChangesAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.GetPullChangesAsync(plain, repo, number, cancellationToken);
    }

    public Task<IReadOnlyList<ReviewDetails>> GetUnaddressedReviewsAsync(string owner, string repo, int number, string botLogin, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.GetUnaddressedReviewsAsync(plain, repo, number, botLogin, since, cancellationToken);
    }

    public Task<RevisionHistory> GetRevisionHistoryAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.GetRevisionHistoryAsync(plain, repo, number, cancellationToken);
    }

    public Task<bool> RequestReviewAsync(string owner, string repo, int number, IReadOnlyList<string> reviewers, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.RequestReviewAsync(plain, repo, number, reviewers, cancellationToken);
    }

    public Task<int?> FindFactoryPullRequestAsync(string owner, string repo, int ticket, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.FindFactoryPullRequestAsync(plain, repo, ticket, cancellationToken);
    }

    public Task<IReadOnlyList<PullSummary>> ListAllPullRequestsAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.ListAllPullRequestsAsync(plain, repo, cancellationToken);
    }

    public Task<PullRequestInfo> CreatePullRequestAsync(string owner, string repo, string head, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.CreatePullRequestAsync(plain, repo, head, baseBranch, title, body, cancellationToken);
    }

    public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.ListBranchesAsync(plain, repo, cancellationToken);
    }

    public Task<bool> IssueIsClosedAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.IssueIsClosedAsync(plain, repo, number, cancellationToken);
    }

    public Task<IReadOnlySet<string>> OpenPullRequestBranchesAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.OpenPullRequestBranchesAsync(plain, repo, cancellationToken);
    }

    public Task DeleteBranchAsync(string owner, string repo, string branch, CancellationToken cancellationToken)
    {
        var (host, plain) = For(owner);
        return host.DeleteBranchAsync(plain, repo, branch, cancellationToken);
    }

    /// <summary>Every host's writable repositories, with the owners qualified. A host that fails fails the whole listing: a partial one must not look like a complete one.</summary>
    public async Task<IReadOnlyList<(string Owner, string Repo)>> ListWritableRepositoriesAsync(CancellationToken cancellationToken)
    {
        var all = new List<(string Owner, string Repo)>();
        foreach (var (name, host) in hosts)
        {
            all.AddRange((await host.ListWritableRepositoriesAsync(cancellationToken)).Select(item => (RepoNames.Qualify(name, item.Owner), item.Repo)));
        }
        return all;
    }
}
