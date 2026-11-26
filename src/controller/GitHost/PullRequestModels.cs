namespace AiSdlcFactory.Controller;

/// <summary>What the factory needs to know about a pull request before it touches the branch.</summary>
public sealed record PullRequestDetails(
    int Number, string Title, string Body, string State, bool Merged, string HeadRef, string HeadSha,
    string HeadRepository, string BaseRepository, string BaseRef, string Author);

/// <summary>
/// One comment of a review: where it is (when it is attached to a line), what it says, and the code it was written on
/// (<see cref="DiffHunk"/>), so a line number that moved since the comment was written does not make it ambiguous.
/// </summary>
public sealed record ReviewComment(
    long Id, string Author, string? Path, int? Line, string Body, string? DiffHunk = null);

/// <summary>A review of the pull request: its overall text and the comments attached to lines.</summary>
public sealed record ReviewDetails(long Id, string Reviewer, string State, string Body, IReadOnlyList<ReviewComment> Comments);

/// <summary>Everything read from the git host to decide whether and what to revise.</summary>
/// <param name="QaFindings">For a <c>/fix qa</c>: the findings of the latest QA report, one string each; null for any other revision.</param>
public sealed record RevisionContext(PullRequestDetails Pull, string BotLogin, IReadOnlyList<ReviewDetails> Reviews, int PriorRevisions, IReadOnlyList<string>? QaFindings = null);

/// <summary>One file a pull request changes: its path, whether it was added, modified, deleted or renamed, and how many lines.</summary>
public sealed record ChangedFile(string Path, string Status, int Additions, int Deletions, string? PreviousPath = null);

/// <summary>
/// What a pull request changes, as the git host reports it: the files (at most <see cref="GitHostLimits.MaxChangedFiles"/>), how many there are in all, and the start of the diff
/// (<see cref="DiffCut"/> when it was longer). Text from the repository, so only ever given to an agent as data.
/// </summary>
public sealed record PullChanges(IReadOnlyList<ChangedFile> Files, int TotalFiles, string Diff, bool DiffCut);

/// <summary>What a QA run needs to know before it touches the branch: the factory's pull request for the ticket (null when there is none), and the bot's login, and what the pull request changes (null when Forgejo could not say).</summary>
public sealed record QaContext(PullRequestDetails? Pull, string BotLogin, PullChanges? Changes = null);

/// <summary>What an <c>/update</c> needs to know before it touches the branch: the factory's pull request for the ticket (null when there is none), and the bot's login.</summary>
public sealed record UpdateContext(PullRequestDetails? Pull, string BotLogin);

/// <summary>Where a ticket's pull request stands: what a comment on a ticket with no running workflow should be told.</summary>
public enum TicketPullState
{
    /// <summary>The factory made no pull request for the ticket (or the host could not say).</summary>
    None,
    Open,
    Merged,
    /// <summary>Closed without being merged.</summary>
    Closed,
}

/// <summary>The state of a ticket's newest pull request and its number (0 when there is none).</summary>
public sealed record TicketPullStatus(TicketPullState State, int Number)
{
    public static readonly TicketPullStatus NoPull = new(TicketPullState.None, 0);

    /// <summary>
    /// The newest pull request the factory (the bot) opened for a ticket from a <c>factory/issue-N</c> branch, open or not. A pull request someone else opened from a branch that
    /// merely has the factory's name does not count.
    /// </summary>
    public static TicketPullStatus Of(int ticket, IEnumerable<PullSummary> pulls, string botLogin)
    {
        PullSummary? newest = null;
        foreach (var pull in pulls)
        {
            if (RevisionRules.TicketOfBranch(pull.HeadRef) == ticket
                && string.Equals(pull.Author, botLogin, StringComparison.OrdinalIgnoreCase)
                && (newest is null || pull.Number > newest.Number))
            {
                newest = pull;
            }
        }
        if (newest is null)
        {
            return NoPull;
        }
        var state = newest.Merged ? TicketPullState.Merged
            : string.Equals(newest.State, "open", StringComparison.OrdinalIgnoreCase) ? TicketPullState.Open
            : TicketPullState.Closed;
        return new TicketPullStatus(state, newest.Number);
    }
}

/// <summary>A plain comment on a pull request: asked whether it is on one of the factory's own open pull requests, to say once that only commands are read there.</summary>
public sealed record PullHintRequest(string Owner, string Repo, int PullNumber, string Author);

/// <summary>How many revisions the factory has made on a pull request, and when the last reply was posted.</summary>
public sealed record RevisionHistory(int Count, DateTimeOffset? LastAt);

/// <summary>What the factory reads of a pull request at most, whatever the host: a huge one is never pulled into memory whole.</summary>
public static class GitHostLimits
{
    /// <summary>Files of a pull request that are listed (the rest are only counted).</summary>
    public const int MaxChangedFiles = 300;

    /// <summary>Characters of its diff that are kept.</summary>
    public const int MaxDiffBytes = 120_000;

    /// <summary>The largest file read from a repository without a checkout.</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>Bytes of its diff that are read at all.</summary>
    public const int MaxRawDiffBytes = 4 * 1024 * 1024;
}

/// <summary>Reads the start of a pull request's diff from a host's response, whatever the host: bounded, without the factory's own run records.</summary>
internal static class DiffReader
{
    /// <summary>
    /// The pull request's changes: the files already listed, and the diff text. It reads far enough to find the code after any run records at the top of the diff (<c>.factory</c> sorts first),
    /// but never more than <see cref="GitHostLimits.MaxRawDiffBytes"/>, and keeps at most <see cref="GitHostLimits.MaxDiffBytes"/> characters of it.
    /// </summary>
    public static async Task<PullChanges> ReadAsync(HttpResponseMessage response, IReadOnlyList<ChangedFile> files, int total, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(GitHostLimits.MaxRawDiffBytes + 1);
        try
        {
            var read = 0;
            while (read <= GitHostLimits.MaxRawDiffBytes)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, GitHostLimits.MaxRawDiffBytes + 1 - read), cancellationToken);
                if (count == 0)
                {
                    break;
                }
                read += count;
            }
            var text = RunRecords.WithoutRecords(System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(read, GitHostLimits.MaxRawDiffBytes)));
            var cut = read > GitHostLimits.MaxRawDiffBytes || text.Length > GitHostLimits.MaxDiffBytes;
            return new PullChanges(files, total, text.Length > GitHostLimits.MaxDiffBytes ? text[..GitHostLimits.MaxDiffBytes] : text, cut);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
