namespace DevFoundry.Controller;

/// <summary>What the factory needs to know about a pull request before it touches the branch.</summary>
public sealed record PullRequestDetails(
    int Number, string Title, string Body, string State, bool Merged, string HeadRef, string HeadSha,
    string HeadRepository, string BaseRepository, string BaseRef, string Author);

/// <summary>One comment of a review: where it is (when it is attached to a line) and what it says.</summary>
public sealed record ReviewComment(long Id, string Author, string? Path, int? Line, string Body);

/// <summary>A review of the pull request: its overall text and the comments attached to lines.</summary>
public sealed record ReviewDetails(long Id, string Reviewer, string State, string Body, IReadOnlyList<ReviewComment> Comments);

/// <summary>Everything read from Forgejo to decide whether and what to revise.</summary>
public sealed record RevisionContext(PullRequestDetails Pull, string BotLogin, ReviewDetails? Review, int PriorRevisions);
