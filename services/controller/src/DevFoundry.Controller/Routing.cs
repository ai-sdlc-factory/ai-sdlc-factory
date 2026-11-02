using System.Text.Json;

namespace DevFoundry.Controller;

/// <summary>A ticket that asks the factory to build a feature.</summary>
public sealed record FeatureRequest(
    string Owner, string Repo, int IssueNumber, string Title, string Body, string Author,
    IReadOnlyList<string>? Labels = null)
{
    public string WorkflowId => FeatureIds.For(Owner, Repo, IssueNumber);
}

/// <summary>A human comment on a ticket that has a feature workflow.</summary>
public sealed record HumanReply(string Author, string Body);

public static class FeatureIds
{
    public static string For(string owner, string repo, int issueNumber)
    {
        var raw = $"feature-{owner}-{repo}-{issueNumber}".ToLowerInvariant();
        return new string(raw.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '-').ToArray());
    }
}

public enum RoutingAction
{
    Ignore,
    StartFeature,
    ReplyToFeature,
    CloseFeature,
}

public sealed record RoutingDecision(
    RoutingAction Action,
    string Reason,
    FeatureRequest? Feature = null,
    HumanReply? Reply = null);

/// <summary>
/// Pure, deterministic mapping from a Forgejo delivery to what the factory should do.
/// Webhook contents are data: nothing here executes anything taken from the payload.
/// </summary>
public static class EventRouter
{
    public const string FeatureLabel = "feature";

    /// <summary>Hidden marker on every comment the factory writes, so it never answers itself.</summary>
    public const string BotMarker = "<!-- dev-foundry:factory -->";

    private static readonly string[] StartingActions = ["opened", "reopened", "label_updated", "labeled"];

    public static RoutingDecision Decide(ForgejoEvent input)
    {
        var payload = input.Payload;
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
            return Ignore("pull request");
        }

        var hasFeatureLabel = HasLabel(issue, FeatureLabel);
        var action = AsString(Get(payload, "action"));
        var isOpen = AsString(Get(issue, "state")) is null or "open";
        var workflowId = FeatureIds.For(owner, repo, issueNumber);

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
                    Feature: new FeatureRequest(
                        owner, repo, issueNumber,
                        AsString(Get(issue, "title")) ?? "",
                        AsString(Get(issue, "body")) ?? "",
                        AsString(Get(Get(issue, "user"), "login")) ?? "",
                        LabelNames(issue)));

            case "issues" when action == "closed":
                return hasFeatureLabel
                    ? new RoutingDecision(RoutingAction.CloseFeature, "ticket closed",
                        Feature: new FeatureRequest(owner, repo, issueNumber, "", "", ""))
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
                return new RoutingDecision(
                    RoutingAction.ReplyToFeature,
                    "human comment",
                    Feature: new FeatureRequest(owner, repo, issueNumber, "", "", ""),
                    Reply: new HumanReply(AsString(Get(Get(Get(payload, "comment"), "user"), "login")) ?? "", body));

            default:
                return Ignore($"unhandled {input.Event}/{action}");
        }
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
