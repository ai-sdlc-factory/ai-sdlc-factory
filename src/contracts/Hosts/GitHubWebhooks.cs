using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSdlcFactory.Contracts;

/// <summary>
/// GitHub (github.com and GitHub Enterprise Server): the signature is a hex HMAC-SHA256 of the exact bytes in <c>X-Hub-Signature-256</c> (<c>sha256=&lt;hex&gt;</c>), events are named in
/// <c>X-GitHub-Event</c>, and a delivery's id is in <c>X-GitHub-Delivery</c>. Issues, comments and reviews come as the <c>issues</c>, <c>issue_comment</c> and <c>pull_request_review</c> events;
/// a comment on a pull request is an <c>issue_comment</c> whose issue has a <c>pull_request</c>. The webhook must be set to send <c>application/json</c>.
/// </summary>
public sealed partial class GitHubWebhookAdapter : IWebhookAdapter
{
    public const string TypeName = "github";

    public string Type => TypeName;
    public string EventHeader => "X-GitHub-Event";
    public string DeliveryHeader => "X-GitHub-Delivery";

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,63}\z")]
    private static partial Regex EventPattern();

    private const string SignaturePrefix = "sha256=";

    /// <summary>The hex digest of the header (<c>sha256=&lt;hex&gt;</c>), or an empty string when the header is not of that shape.</summary>
    private static string Digest(Func<string, string> header) =>
        header("X-Hub-Signature-256") is { } value && value.StartsWith(SignaturePrefix, StringComparison.Ordinal) ? value[SignaturePrefix.Length..] : "";

    public bool HasWellFormedSignature(Func<string, string> header) => HmacSignature.IsWellFormed(Digest(header));

    public bool IsValidSignature(Func<string, string> header, byte[] body, string secret) => HmacSignature.IsValid(secret, body, Digest(header));

    public string? EventName(Func<string, string> header) => EventPattern().IsMatch(header(EventHeader)) ? header(EventHeader) : null;

    public string? DeliveryId(Func<string, string> header) => Guid.TryParse(header(DeliveryHeader), out var delivery) ? delivery.ToString() : null;

    public RepoEvent Normalise(WebhookDelivery delivery)
    {
        var payload = delivery.Payload;
        var action = WebhookJson.AsString(WebhookJson.Get(payload, "action"));
        var repository = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(payload, "repository"), "full_name"));
        RepoEvent Make(RepoEventKind kind) => new(delivery.Host, delivery.DeliveryId, delivery.Event, kind, action, WebhookJson.AsString(WebhookJson.Get(payload, "ref")), repository);

        // A review that asks for changes: GitHub sends every review as pull_request_review/submitted, with the verdict in review.state (lower case in a webhook, upper case in the API).
        if (delivery.Event == "pull_request_review")
        {
            if (!WebhookJson.TryOwnerRepo(repository, out var reviewOwner, out var reviewRepo)
                || !WebhookJson.TryGet(payload, "pull_request", out var pull)
                || WebhookJson.Get(pull, "number") is not { ValueKind: JsonValueKind.Number } pullNumber || !pullNumber.TryGetInt32(out var number))
            {
                return Make(RepoEventKind.MalformedReview);
            }
            var review = WebhookJson.Get(payload, "review");
            var asksForChanges = action == "submitted" && string.Equals(WebhookJson.AsString(WebhookJson.Get(review, "state")), "changes_requested", StringComparison.OrdinalIgnoreCase);
            return Make(asksForChanges ? RepoEventKind.ChangesRequested : RepoEventKind.PullRequestActivity) with
            {
                Owner = RepoNames.Qualify(delivery.Host, reviewOwner), Repo = reviewRepo, Number = number, IsPullRequest = true,
                HeadRef = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(pull, "head"), "ref")), PullState = WebhookJson.AsString(WebhookJson.Get(pull, "state")),
                ReviewBody = WebhookJson.AsString(WebhookJson.Get(review, "body")) ?? "",
                Actor = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(payload, "sender"), "login")) ?? WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(review, "user"), "login")) ?? "",
            };
        }

        if (!WebhookJson.TryOwnerRepo(repository, out var owner, out var repo)
            || !WebhookJson.TryGet(payload, "issue", out var issue)
            || WebhookJson.Get(issue, "number") is not { ValueKind: JsonValueKind.Number } issueNumber || !issueNumber.TryGetInt32(out var ticket))
        {
            return Make(RepoEventKind.Unrelated);
        }

        // A comment on a pull request comes as an issue comment of an issue that has a pull_request.
        var isPull = WebhookJson.Get(issue, "pull_request") is { ValueKind: JsonValueKind.Object };
        return Make(Kind(delivery.Event, action, isPull)) with
        {
            Owner = RepoNames.Qualify(delivery.Host, owner), Repo = repo, Number = ticket, IsPullRequest = isPull,
            IsOpen = WebhookJson.AsString(WebhookJson.Get(issue, "state")) is null or "open",
            Title = WebhookJson.AsString(WebhookJson.Get(issue, "title")) ?? "", Body = WebhookJson.AsString(WebhookJson.Get(issue, "body")) ?? "",
            Author = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(issue, "user"), "login")) ?? "",
            // Who set the event off (who added the label, opened or reopened the ticket); the author of the ticket is another thing.
            Actor = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(payload, "sender"), "login")) ?? "",
            Labels = WebhookJson.LabelNames(issue),
            // GitHub's issue types (a bug, a task, a feature) are the work-item types of a ticket there.
            ItemType = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(issue, "type"), "name")),
            CommentBody = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(payload, "comment"), "body")) ?? "",
            CommentAuthor = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(WebhookJson.Get(payload, "comment"), "user"), "login")) ?? "",
        };
    }

    private static RepoEventKind Kind(string eventName, string? action, bool isPull)
    {
        if (isPull)
        {
            return eventName == "issue_comment" && action == "created" ? RepoEventKind.Commented : RepoEventKind.PullRequestActivity;
        }
        return (eventName, action) switch
        {
            ("issues", "reopened") => RepoEventKind.TicketReopened,
            ("issues", "opened" or "labeled") => RepoEventKind.TicketOpened,
            ("issues", "closed") => RepoEventKind.TicketClosed,
            ("issue_comment", "created") => RepoEventKind.Commented,
            _ => RepoEventKind.Other,
        };
    }
}
