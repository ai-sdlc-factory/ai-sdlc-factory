using System.Text.RegularExpressions;

namespace AiSdlcFactory.Controller;

/// <summary>
/// Which commands a person's comment or review carries, in the words a rule uses (<see cref="CommandNames"/>). A rule of a single command (<c>"approve": "admin"</c>) is applied to every command
/// the comment carries, so a comment cannot hide a restricted command behind a permitted one on another line.
/// </summary>
public static partial class CommandNameReader
{
    [GeneratedRegex(@"^[ \t]*/(?<name>[a-z]+)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Line();

    /// <summary>The commands of this event, which led to this decision: always at least one (a plain answer to an agent is <c>reply</c>, a review that asks for changes <c>changes</c>).</summary>
    public static IReadOnlyList<string> Of(RepoEvent input, RoutingDecision decision)
    {
        if (input.Kind == RepoEventKind.ChangesRequested)
        {
            return ["changes"];
        }
        var names = new List<string>();
        foreach (Match match in Line().Matches(input.CommentBody ?? ""))
        {
            if (Canonical(match.Groups["name"].Value.ToLowerInvariant()) is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }
        if (names.Count == 0)
        {
            // The router decided without a slash word the factory knows: say what the decision was.
            names.Add(decision.Action switch
            {
                RoutingAction.RunSetup => "setup",
                RoutingAction.AskQuestion => "ask",
                RoutingAction.RetryFeature => "retry",
                RoutingAction.RunQa or RoutingAction.ConfirmQa => "qa",
                RoutingAction.UpdatePullRequest => "update",
                RoutingAction.ReviewPullRequest => "review",
                RoutingAction.RevisePullRequest => "fix",
                RoutingAction.StartFeature => "factory",
                _ => "reply",
            });
        }
        return names;
    }

    private static string? Canonical(string word) => word switch
    {
        "revise" => "fix",
        "choose" => "option",
        _ when CommandNames.All.Contains(word) && word is not ("reply" or "changes") => word,
        _ => null,
    };
}
