using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSdlcFactory.Contracts;

/// <summary>Forgejo: HMAC-SHA256 over the exact bytes in <c>X-Forgejo-Signature</c>, events named in <c>X-Forgejo-Event</c>, issues, comments and reviews in its own JSON.</summary>
public sealed partial class ForgejoWebhookAdapter : IWebhookAdapter
{
    public const string TypeName = "forgejo";

    public string Type => TypeName;
    public string EventHeader => "X-Forgejo-Event";
    public string DeliveryHeader => "X-Forgejo-Delivery";

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,63}\z")]
    private static partial Regex EventPattern();

    public bool HasWellFormedSignature(Func<string, string> header) => HmacSignature.IsWellFormed(header("X-Forgejo-Signature"));

    public bool IsValidSignature(Func<string, string> header, byte[] body, string secret) => HmacSignature.IsValid(secret, body, header("X-Forgejo-Signature"));

    public string? EventName(Func<string, string> header) => EventPattern().IsMatch(header(EventHeader)) ? header(EventHeader) : null;

    public string? DeliveryId(Func<string, string> header) => Guid.TryParse(header(DeliveryHeader), out var delivery) ? delivery.ToString() : null;

    private static readonly string[] StartingActions = ["opened", "reopened", "label_updated", "labeled"];

    public RepoEvent Normalise(WebhookDelivery delivery)
    {
        var payload = delivery.Payload;
        var action = WebhookJson.AsString(WebhookJson.Get(payload, "action"));
        var repository = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(payload, "repository"), "full_name"));
        RepoEvent Make(RepoEventKind kind) => new(delivery.Host, delivery.DeliveryId, delivery.Event, kind, action, WebhookJson.AsString(WebhookJson.Get(payload, "ref")), repository);

        // A review that requests changes carries the pull request, not a ticket. Forgejo names this event "pull_request_rejected" in its header
        // (the payload's review.type says "pull_request_review_rejected").
        if (delivery.Event is "pull_request_rejected" or "pull_request_review_rejected")
        {
            if (!WebhookJson.TryOwnerRepo(repository, out var reviewOwner, out var reviewRepo)
                || !WebhookJson.TryGet(payload, "pull_request", out var pull)
                || WebhookJson.Get(pull, "number") is not { ValueKind: JsonValueKind.Number } pullNumber || !pullNumber.TryGetInt32(out var number))
            {
                return Make(RepoEventKind.MalformedReview);
            }
            return Make(RepoEventKind.ChangesRequested) with
            {
                Owner = RepoNames.Qualify(delivery.Host, reviewOwner), Repo = reviewRepo, Number = number, IsPullRequest = true,
                HeadRef = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(pull, "head"), "ref")), PullState = WebhookJson.AsString(WebhookJson.Get(pull, "state")),
                ReviewBody = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(payload, "review"), "content")) ?? "", Actor = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(payload, "sender"), "login")) ?? "",
            };
        }

        if (!WebhookJson.TryOwnerRepo(repository, out var owner, out var repo)
            || !WebhookJson.TryGet(payload, "issue", out var issue)
            || WebhookJson.Get(issue, "number") is not { ValueKind: JsonValueKind.Number } issueNumber || !issueNumber.TryGetInt32(out var ticket))
        {
            return Make(RepoEventKind.Unrelated);
        }

        // Forgejo reports pull request activity through the same events. Plain tickets carry "pull_request": null, so only a real object counts.
        var isPull = WebhookJson.Get(payload, "is_pull") is { ValueKind: JsonValueKind.True } || WebhookJson.Get(issue, "pull_request") is { ValueKind: JsonValueKind.Object };
        var kind = Kind(delivery.Event, action, isPull);
        return Make(kind) with
        {
            Owner = RepoNames.Qualify(delivery.Host, owner), Repo = repo, Number = ticket, IsPullRequest = isPull,
            IsOpen = WebhookJson.AsString(WebhookJson.Get(issue, "state")) is null or "open",
            Title = WebhookJson.AsString(WebhookJson.Get(issue, "title")) ?? "", Body = WebhookJson.AsString(WebhookJson.Get(issue, "body")) ?? "", Author = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(issue, "user"), "login")) ?? "",
            // Who set the event off (who added the label, opened or reopened the ticket); the author of the ticket is another thing.
            Actor = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(payload, "sender"), "login")) ?? "",
            Labels = WebhookJson.Get(issue, "labels") is { ValueKind: JsonValueKind.Array } labels
                ? labels.EnumerateArray().Select(item => WebhookJson.AsString(WebhookJson.Get(item, "name"))).OfType<string>().ToList()
                : [],
            CommentBody = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(payload, "comment"), "body")) ?? "",
            CommentAuthor = WebhookJson.AsString(WebhookJson.Get(WebhookJson.Get(WebhookJson.Get(payload, "comment"), "user"), "login")) ?? "",
        };
    }

    private static RepoEventKind Kind(string eventName, string? action, bool isPull)
    {
        if (isPull)
        {
            // A comment on a pull request can carry a command; everything else about a pull request is nothing to do.
            return eventName is "issue_comment" or "pull_request_comment" && action == "created" ? RepoEventKind.Commented : RepoEventKind.PullRequestActivity;
        }
        return (eventName, action) switch
        {
            ("issues", "reopened") => RepoEventKind.TicketReopened,
            ("issues", not null) when StartingActions.Contains(action) => RepoEventKind.TicketOpened,
            ("issues", "closed") => RepoEventKind.TicketClosed,
            ("issue_comment", "created") => RepoEventKind.Commented,
            _ => RepoEventKind.Other,
        };
    }
}
