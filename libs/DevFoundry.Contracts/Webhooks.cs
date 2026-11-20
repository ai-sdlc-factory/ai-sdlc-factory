using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFoundry.Contracts;

/// <summary>What a webhook delivery is about, once a host's own words are put into the factory's.</summary>
public enum RepoEventKind
{
    /// <summary>Not about a ticket or a pull request the factory can know (no repository, no number).</summary>
    Unrelated,

    /// <summary>A ticket was opened or its labels changed.</summary>
    TicketOpened,

    /// <summary>A ticket was reopened (the way to run the factory on it again).</summary>
    TicketReopened,

    /// <summary>A ticket was closed.</summary>
    TicketClosed,

    /// <summary>A comment was written on a ticket or on a pull request (<see cref="RepoEvent.IsPullRequest"/> says which).</summary>
    Commented,

    /// <summary>Something else happened to a pull request (opened, edited, closed, a comment changed): nothing to do.</summary>
    PullRequestActivity,

    /// <summary>A review asked for changes on a pull request.</summary>
    ChangesRequested,

    /// <summary>A review event without the pull request it is about.</summary>
    MalformedReview,

    /// <summary>A ticket event the factory has no use for.</summary>
    Other,
}

/// <summary>
/// A webhook delivery in the factory's own words, whichever host sent it: what the router decides on. Only data the router needs; the host's raw JSON stays in the
/// workflow's input. Texts (<see cref="Title"/>, <see cref="Body"/>, <see cref="CommentBody"/>, <see cref="ReviewBody"/>) are people's words: data, never instructions.
/// </summary>
/// <param name="Host">The name of the host the delivery came from (<c>forgejo</c>, or one of the extra hosts).</param>
/// <param name="EventName">The host's own event name, for the receipt and the log.</param>
/// <param name="Action">The host's own action word (<c>opened</c>, <c>created</c>), or null.</param>
/// <param name="Reference">The host's <c>ref</c> of the delivery (a push), or null.</param>
/// <param name="Repository">The repository as the host names it (<c>owner/name</c>), or null when the delivery has none.</param>
/// <param name="Owner">The owner as the factory names it: plain for the default host, <c>host:owner</c> for any other (see <see cref="RepoNames"/>).</param>
/// <param name="Number">The ticket or pull request number (for a review: the pull request's).</param>
/// <param name="IsOpen">The ticket is open (or says nothing about it).</param>
/// <param name="Author">Who wrote the ticket.</param>
/// <param name="CommentAuthor">Who wrote the comment.</param>
/// <param name="HeadRef">For a review: the branch the pull request comes from.</param>
/// <param name="PullState">For a review: the pull request's state as the host says it (<c>open</c>), or null when it says none.</param>
/// <param name="Actor">For a review: who submitted it.</param>
public sealed record RepoEvent(
    string Host, string DeliveryId, string EventName, RepoEventKind Kind, string? Action = null, string? Reference = null, string? Repository = null,
    string Owner = "", string Repo = "", int Number = 0, bool IsPullRequest = false, bool IsOpen = true,
    string Title = "", string Body = "", string Author = "", IReadOnlyList<string>? Labels = null,
    string CommentBody = "", string CommentAuthor = "",
    string? HeadRef = null, string? PullState = null, string ReviewBody = "", string Actor = "")
{
    public IReadOnlyList<string> LabelNames => Labels ?? [];

    public bool HasLabel(string label) => LabelNames.Any(name => string.Equals(name, label, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What the factory needs from one git host's webhooks: how to tell a genuine delivery from a forged one, and how to read it. The API verifies and forwards; the controller
/// normalises. A host decides nothing: it only carries events in.
/// </summary>
public interface IWebhookAdapter
{
    /// <summary>The kind of host this adapter reads (<c>forgejo</c>). A factory can have several hosts of one kind; each has a name of its own (see <see cref="WebhookDelivery.Host"/>).</summary>
    string Type { get; }

    /// <summary>The headers the delivery must carry, named for the error messages.</summary>
    string EventHeader { get; }

    string DeliveryHeader { get; }

    /// <summary>The signature header is there and has the shape of a signature, so the body is worth reading.</summary>
    bool HasWellFormedSignature(Func<string, string> header);

    /// <summary>The signature matches the exact bytes of the body for this secret (a constant-time comparison).</summary>
    bool IsValidSignature(Func<string, string> header, byte[] body, string secret);

    /// <summary>The event name the delivery carries, or null when it is missing or not of a safe shape.</summary>
    string? EventName(Func<string, string> header);

    /// <summary>The delivery's own id, or null when it is missing or not of a safe shape.</summary>
    string? DeliveryId(Func<string, string> header);

    /// <summary>Reads the delivery in the factory's words. Pure: the same delivery always gives the same event.</summary>
    RepoEvent Normalise(WebhookDelivery delivery);
}

/// <summary>The hosts the factory can take webhooks from.</summary>
public static class WebhookAdapters
{
    public static readonly IReadOnlyList<IWebhookAdapter> All = [new ForgejoWebhookAdapter()];

    /// <summary>The adapter for a kind of host (<c>forgejo</c>), or null.</summary>
    public static IWebhookAdapter? Find(string type) => All.FirstOrDefault(adapter => string.Equals(adapter.Type, type, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The signature scheme Forgejo (and GitHub) use: a hex HMAC-SHA256 of the exact bytes of the body. One implementation, for the adapters and for the API's own tests of it.</summary>
public static partial class HmacSignature
{
    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z")]
    private static partial Regex Hex64();

    /// <summary>True when the text looks like a 64-character hex SHA-256 digest.</summary>
    public static bool IsWellFormed(string signature) => Hex64().IsMatch(signature);

    /// <summary>Constant-time check of a hex HMAC-SHA256 signature; false for one that is not well formed.</summary>
    public static bool IsValid(string secret, byte[] body, string signatureHex)
    {
        if (!IsWellFormed(signatureHex))
        {
            return false;
        }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signatureHex));
    }
}

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
        var action = AsString(Get(payload, "action"));
        var repository = AsString(Get(Get(payload, "repository"), "full_name"));
        RepoEvent Make(RepoEventKind kind) => new(delivery.Host, delivery.DeliveryId, delivery.Event, kind, action, AsString(Get(payload, "ref")), repository);

        // A review that requests changes carries the pull request, not a ticket. Forgejo names this event "pull_request_rejected" in its header
        // (the payload's review.type says "pull_request_review_rejected").
        if (delivery.Event is "pull_request_rejected" or "pull_request_review_rejected")
        {
            if (!TryOwnerRepo(repository, out var reviewOwner, out var reviewRepo)
                || !TryGet(payload, "pull_request", out var pull)
                || Get(pull, "number") is not { ValueKind: JsonValueKind.Number } pullNumber || !pullNumber.TryGetInt32(out var number))
            {
                return Make(RepoEventKind.MalformedReview);
            }
            return Make(RepoEventKind.ChangesRequested) with
            {
                Owner = RepoNames.Qualify(delivery.Host, reviewOwner), Repo = reviewRepo, Number = number, IsPullRequest = true,
                HeadRef = AsString(Get(Get(pull, "head"), "ref")), PullState = AsString(Get(pull, "state")),
                ReviewBody = AsString(Get(Get(payload, "review"), "content")) ?? "", Actor = AsString(Get(Get(payload, "sender"), "login")) ?? "",
            };
        }

        if (!TryOwnerRepo(repository, out var owner, out var repo)
            || !TryGet(payload, "issue", out var issue)
            || Get(issue, "number") is not { ValueKind: JsonValueKind.Number } issueNumber || !issueNumber.TryGetInt32(out var ticket))
        {
            return Make(RepoEventKind.Unrelated);
        }

        // Forgejo reports pull request activity through the same events. Plain tickets carry "pull_request": null, so only a real object counts.
        var isPull = Get(payload, "is_pull") is { ValueKind: JsonValueKind.True } || Get(issue, "pull_request") is { ValueKind: JsonValueKind.Object };
        var kind = Kind(delivery.Event, action, isPull);
        return Make(kind) with
        {
            Owner = RepoNames.Qualify(delivery.Host, owner), Repo = repo, Number = ticket, IsPullRequest = isPull,
            IsOpen = AsString(Get(issue, "state")) is null or "open",
            Title = AsString(Get(issue, "title")) ?? "", Body = AsString(Get(issue, "body")) ?? "", Author = AsString(Get(Get(issue, "user"), "login")) ?? "",
            Labels = Get(issue, "labels") is { ValueKind: JsonValueKind.Array } labels
                ? labels.EnumerateArray().Select(item => AsString(Get(item, "name"))).OfType<string>().ToList()
                : [],
            CommentBody = AsString(Get(Get(payload, "comment"), "body")) ?? "",
            CommentAuthor = AsString(Get(Get(Get(payload, "comment"), "user"), "login")) ?? "",
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

    private static bool TryOwnerRepo(string? fullName, out string owner, out string repo)
    {
        owner = repo = "";
        if (fullName?.Split('/') is not [var o, var r])
        {
            return false;
        }
        owner = o;
        repo = r;
        return true;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static JsonElement Get(JsonElement element, string name) => TryGet(element, name, out var value) ? value : default;

    private static string? AsString(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
