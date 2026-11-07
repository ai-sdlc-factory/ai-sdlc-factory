using System.Text.Json;

namespace DevFoundry.Controller;

public enum RoutingAction
{
    Ignore,
    StartFeature,
    ReplyToFeature,
    CloseFeature,
    RevisePullRequest,
    RetryFeature,
    RunQa,
}

public sealed record RoutingDecision(
    RoutingAction Action,
    string Reason,
    WorkRequest? Feature = null,
    HumanReply? Reply = null,
    bool Restart = false,
    RevisionRequest? Revision = null,
    QaRequest? Qa = null);

/// <summary>
/// Pure, deterministic mapping from a Forgejo delivery to what the factory should do.
/// Webhook contents are data: nothing here executes anything taken from the payload.
/// </summary>
public static partial class EventRouter
{
    public const string FeatureLabel = "feature";
    public const string BugLabel = "bug";
    public const string IncidentLabel = "incident";

    /// <summary>Hidden marker on every comment the factory writes, so it never answers itself.</summary>
    public const string BotMarker = "<!-- dev-foundry:factory -->";

    private static readonly string[] StartingActions = ["opened", "reopened", "label_updated", "labeled"];

    public static RoutingDecision Decide(ForgejoEvent input)
    {
        var payload = input.Payload;
        // A review that requests changes carries the pull request, not a ticket.
        // Forgejo names this event "pull_request_rejected" in its header (the payload's review.type says "pull_request_review_rejected").
        if (input.Event is "pull_request_rejected" or "pull_request_review_rejected")
        {
            return DecideReview(payload);
        }
        if (!TryGet(payload, "repository", out var repository)
            || AsString(Get(repository, "full_name")) is not { } fullName
            || fullName.Split('/') is not [var owner, var repo]
            || !TryGet(payload, "issue", out var issue)
            || Get(issue, "number") is not { ValueKind: JsonValueKind.Number } number
            || !number.TryGetInt32(out var issueNumber))
        {
            return Ignore("not a ticket event");
        }

        // Forgejo reports pull request activity through the same events. Plain tickets carry
        // "pull_request": null, so only a real object counts.
        if (Get(payload, "is_pull") is { ValueKind: JsonValueKind.True }
            || Get(issue, "pull_request") is { ValueKind: JsonValueKind.Object })
        {
            return DecidePullRequestComment(input, owner, repo, issueNumber);
        }

        return DecideTicketEvent(input, issue, owner, repo, issueNumber);
    }

    /// <summary>What a ticket event means: start the ticket's workflow, tell it the ticket closed, or hand it a person's comment.</summary>
    private static RoutingDecision DecideTicketEvent(ForgejoEvent input, JsonElement issue, string owner, string repo, int issueNumber)
    {
        var payload = input.Payload;
        // An incident is never built as a feature or a bug; a feature label wins over a bug label.
        var kind = HasLabel(issue, IncidentLabel) ? WorkIds.Incident
            : HasLabel(issue, FeatureLabel) ? WorkIds.Feature
            : HasLabel(issue, BugLabel) ? WorkIds.Bug : null;
        var hasFeatureLabel = kind is not null;
        var action = AsString(Get(payload, "action"));
        var isOpen = AsString(Get(issue, "state")) is null or "open";

        switch (input.Event)
        {
            case "issues" when action is not null && StartingActions.Contains(action):
                if (!hasFeatureLabel || !isOpen)
                {
                    return Ignore("ticket is not an open feature");
                }
                return new RoutingDecision(
                    RoutingAction.StartFeature,
                    "feature ticket",
                    // Reopening a ticket is the way to run the factory on it again.
                    Restart: action == "reopened",
                    Feature: TicketRequest(issue, owner, repo, issueNumber, kind!));

            case "issues" when action == "closed":
                return hasFeatureLabel
                    ? new RoutingDecision(RoutingAction.CloseFeature, "ticket closed",
                        Feature: new WorkRequest(owner, repo, issueNumber, "", "", "", Kind: kind!))
                    : Ignore("ticket is not a feature");

            case "issue_comment" when action == "created":
                var body = AsString(Get(Get(payload, "comment"), "body")) ?? "";
                if (!hasFeatureLabel)
                {
                    return Ignore("ticket is not a feature");
                }
                if (body.Contains(BotMarker, StringComparison.Ordinal))
                {
                    return Ignore("comment written by the factory");
                }
                if (QaCommand.Parse(body) is { } qa)
                {
                    // Only the factory's feature and bug tickets have a pull request to test.
                    return kind is WorkIds.Feature or WorkIds.Bug
                        ? new RoutingDecision(RoutingAction.RunQa, "qa command on a ticket",
                            Qa: new QaRequest(owner, repo, issueNumber, false, qa.Suite, AsString(Get(Get(Get(payload, "comment"), "user"), "login")) ?? "", qa.Mode, qa.Note))
                        : Ignore("qa needs a feature or bug ticket");
                }
                // `/retry` starts the ticket's run again, like closing and reopening it, without touching the ticket.
                if (RetryCommand().IsMatch(body))
                {
                    return isOpen
                        ? new RoutingDecision(RoutingAction.RetryFeature, "retry", Restart: true, Feature: TicketRequest(issue, owner, repo, issueNumber, kind!))
                        : Ignore("ticket is closed");
                }
                return new RoutingDecision(
                    RoutingAction.ReplyToFeature,
                    "human comment",
                    Feature: new WorkRequest(owner, repo, issueNumber, "", "", "", Kind: kind!),
                    Reply: new HumanReply(AsString(Get(Get(Get(payload, "comment"), "user"), "login")) ?? "", body));

            default:
                return Ignore($"unhandled {input.Event}/{action}");
        }
    }

    /// <summary>The ticket as the workflows get it: its text and labels as they are now.</summary>
    private static WorkRequest TicketRequest(JsonElement issue, string owner, string repo, int issueNumber, string kind) =>
        new(owner, repo, issueNumber,
            AsString(Get(issue, "title")) ?? "",
            AsString(Get(issue, "body")) ?? "",
            AsString(Get(Get(issue, "user"), "login")) ?? "",
            LabelNames(issue),
            kind);

    /// <summary>A line that is just <c>/retry</c> on a ticket: run the factory on it again.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*/retry[ \t]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex RetryCommand();

    /// <summary>A line starting with <c>/fix</c> (or <c>/revise</c>) on a pull request; what follows it says what to change.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*/(?:fix|revise)\b(?<rest>[^\r\n]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex FixCommand();

    /// <summary>
    /// A comment on a pull request does nothing, except one with the <c>/fix</c> command: that asks the factory to revise its own pull
    /// request. Whether the pull request is the factory's is checked when the revision starts, because the comment does not say.
    /// </summary>
    private static RoutingDecision DecidePullRequestComment(ForgejoEvent input, string owner, string repo, int pullNumber)
    {
        if (input.Event is not ("issue_comment" or "pull_request_comment") || AsString(Get(input.Payload, "action")) != "created")
        {
            return Ignore("pull request");
        }
        var comment = Get(input.Payload, "comment");
        var body = AsString(Get(comment, "body")) ?? "";
        if (body.Contains(BotMarker, StringComparison.Ordinal))
        {
            return Ignore("comment written by the factory");
        }
        if (QaCommand.Parse(body) is { } qa)
        {
            return new RoutingDecision(RoutingAction.RunQa, "qa command on a pull request",
                Qa: new QaRequest(owner, repo, pullNumber, true, qa.Suite, AsString(Get(Get(comment, "user"), "login")) ?? "", qa.Mode, qa.Note));
        }
        var match = FixCommand().Match(body);
        if (!match.Success)
        {
            return Ignore("pull request");
        }
        var instruction = (match.Groups["rest"].Value + body[(match.Index + match.Length)..]).Trim();
        var author = AsString(Get(Get(comment, "user"), "login")) ?? "";
        return new RoutingDecision(
            RoutingAction.RevisePullRequest, "fix command on a pull request",
            Revision: new RevisionRequest(owner, repo, pullNumber, RevisionRequest.ByCommand, instruction, author));
    }

    /// <summary>A review that asks for changes on one of the factory's own branches starts a revision.</summary>
    private static RoutingDecision DecideReview(JsonElement payload)
    {
        if (!TryGet(payload, "repository", out var repository)
            || AsString(Get(repository, "full_name")) is not { } fullName
            || fullName.Split('/') is not [var owner, var repo]
            || !TryGet(payload, "pull_request", out var pull)
            || Get(pull, "number") is not { ValueKind: JsonValueKind.Number } number
            || !number.TryGetInt32(out var pullNumber))
        {
            return Ignore("not a pull request review");
        }
        var headRef = AsString(Get(Get(pull, "head"), "ref"));
        if (headRef is null || !RevisionRules.FactoryBranch().IsMatch(headRef))
        {
            return Ignore("not a factory pull request");
        }
        if (AsString(Get(pull, "state")) is { } state && state != "open")
        {
            return Ignore("pull request is not open");
        }
        var content = AsString(Get(Get(payload, "review"), "content")) ?? "";
        if (content.Contains(BotMarker, StringComparison.Ordinal))
        {
            return Ignore("review written by the factory");
        }
        var reviewer = AsString(Get(Get(payload, "sender"), "login")) ?? "";
        return new RoutingDecision(
            RoutingAction.RevisePullRequest, "review requested changes",
            Revision: new RevisionRequest(owner, repo, pullNumber, RevisionRequest.ByReview, content.Trim(), reviewer));
    }

    private static RoutingDecision Ignore(string reason) => new(RoutingAction.Ignore, reason);

    private static List<string> LabelNames(JsonElement issue) =>
        Get(issue, "labels") is { ValueKind: JsonValueKind.Array } labels
            ? labels.EnumerateArray().Select(item => AsString(Get(item, "name"))).OfType<string>().ToList()
            : [];

    private static bool HasLabel(JsonElement issue, string label) =>
        Get(issue, "labels") is { ValueKind: JsonValueKind.Array } labels
        && labels.EnumerateArray().Any(item =>
            string.Equals(AsString(Get(item, "name")), label, StringComparison.OrdinalIgnoreCase));

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static JsonElement Get(JsonElement element, string name) =>
        TryGet(element, name, out var value) ? value : default;

    private static string? AsString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
