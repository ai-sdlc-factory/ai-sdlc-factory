namespace DevFoundry.Controller;

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
/// What a pull request changes, as the git host reports it: the files (at most <see cref="ForgejoClient.MaxChangedFiles"/>), how many there are in all, and the start of the diff
/// (<see cref="DiffCut"/> when it was longer). Text from the repository, so only ever given to an agent as data.
/// </summary>
public sealed record PullChanges(IReadOnlyList<ChangedFile> Files, int TotalFiles, string Diff, bool DiffCut);

/// <summary>What a QA run needs to know before it touches the branch: the factory's pull request for the ticket (null when there is none), and the bot's login, and what the pull request changes (null when Forgejo could not say).</summary>
public sealed record QaContext(PullRequestDetails? Pull, string BotLogin, PullChanges? Changes = null);

/// <summary>What an <c>/update</c> needs to know before it touches the branch: the factory's pull request for the ticket (null when there is none), and the bot's login.</summary>
public sealed record UpdateContext(PullRequestDetails? Pull, string BotLogin);

/// <summary>How many revisions the factory has made on a pull request, and when the last reply was posted.</summary>
public sealed record RevisionHistory(int Count, DateTimeOffset? LastAt);
