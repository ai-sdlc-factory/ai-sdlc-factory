namespace AiSdlcFactory.Controller;

public enum RoutingAction
{
    Ignore,
    StartFeature,
    ReplyToFeature,
    CloseFeature,
    RevisePullRequest,
    RetryFeature,
    RunQa,
    ConfirmQa,
    UpdatePullRequest,
    ReviewPullRequest,
    AskQuestion,
    RunSetup,
}

public sealed record RoutingDecision(
    RoutingAction Action,
    string Reason,
    WorkRequest? Feature = null,
    HumanReply? Reply = null,
    bool Restart = false,
    RevisionRequest? Revision = null,
    QaRequest? Qa = null,
    UpdateRequest? Update = null,
    PullReviewRequest? Review = null,
    AskRequest? Ask = null,
    WorkRequest? Setup = null);

/// <summary>
/// Pure, deterministic mapping from a webhook delivery (read by its host's adapter into a <see cref="RepoEvent"/>) to what the factory should do.
/// Webhook contents are data: nothing here executes anything taken from the payload.
/// </summary>
public static partial class EventRouter
{
    public const string FeatureLabel = "feature";
    public const string BugLabel = "bug";
    public const string IncidentLabel = "incident";
    public const string PlanLabel = "plan";
    public const string DocsLabel = "docs";

    /// <summary>Hidden marker on every comment the factory writes, so it never answers itself.</summary>
    public const string BotMarker = "<!-- ai-sdlc-factory:factory -->";

    public static RoutingDecision Decide(RepoEvent input, TriggerRules? triggers = null)
    {
        switch (input.Kind)
        {
            case RepoEventKind.ChangesRequested:
                return DecideReview(input);
            case RepoEventKind.MalformedReview:
                return Ignore("not a pull request review");
            case RepoEventKind.Unrelated:
                return Ignore("not a ticket event");
            case RepoEventKind.PullRequestActivity:
                return Ignore("pull request");
            case RepoEventKind.Commented when input.IsPullRequest:
                return DecidePullRequestComment(input);
            default:
                return DecideTicketEvent(input, triggers ?? TriggerRules.Default);
        }
    }

    /// <summary>What a ticket event means: start the ticket's workflow, tell it the ticket closed, or hand it a person's comment.</summary>
    private static RoutingDecision DecideTicketEvent(RepoEvent input, TriggerRules triggers)
    {
        var (owner, repo, issueNumber) = (input.Owner, input.Repo, input.Number);
        // The ticket says which workflow it wants by a label (or tag, or work-item type, as the host's triggers say). An incident is never built as a feature or a bug,
        // and a plan is split into tickets, not built; a feature wins over a bug (see TriggerRules.Kinds).
        var kind = triggers.KindOf(input);
        var hasFeatureLabel = kind is not null;
        var isOpen = input.IsOpen;
        var commentAuthor = input.CommentAuthor;
        var body = input.CommentBody;

        switch (input.Kind)
        {
            case RepoEventKind.TicketOpened or RepoEventKind.TicketReopened:
                if (!hasFeatureLabel || !isOpen)
                {
                    return Ignore("ticket is not an open feature");
                }
                return new RoutingDecision(
                    RoutingAction.StartFeature,
                    "feature ticket",
                    // Reopening a ticket is the way to run the factory on it again.
                    Restart: input.Kind == RepoEventKind.TicketReopened,
                    Feature: TicketRequest(input, kind!));

            case RepoEventKind.TicketClosed:
                return hasFeatureLabel
                    ? new RoutingDecision(RoutingAction.CloseFeature, "ticket closed",
                        Feature: new WorkRequest(owner, repo, issueNumber, "", "", "", Kind: kind!))
                    : Ignore("ticket is not a feature");

            case RepoEventKind.Commented:
                if (body.Contains(BotMarker, StringComparison.Ordinal))
                {
                    return Ignore("comment written by the factory");
                }
                // A question needs no factory label (any ticket can be asked about), and it is never a reply to an agent.
                if (AskCommand.Parse(body) is { } question)
                {
                    return new RoutingDecision(RoutingAction.AskQuestion, "ask command on a ticket",
                        Ask: new AskRequest(owner, repo, issueNumber, false, commentAuthor, question, input.Title, input.Body));
                }
                // Setting up a repository needs no factory label either: a repository without settings has no use for labels yet.
                if (SetupCommand.Matches(body))
                {
                    return new RoutingDecision(RoutingAction.RunSetup, "setup command on a ticket",
                        Setup: new WorkRequest(owner, repo, issueNumber, input.Title, input.Body, commentAuthor, Kind: WorkIds.Setup));
                }
                // `/factory <kind>` asks for a workflow on a ticket that has not asked for one, where the host's triggers allow it (off by default: see TriggerRules).
                if (!hasFeatureLabel && triggers.Command && FactoryCommand.Parse(body) is { } asked)
                {
                    return isOpen
                        ? new RoutingDecision(RoutingAction.StartFeature, "factory command on a ticket", Feature: TicketRequest(input, asked))
                        : Ignore("ticket is closed");
                }
                if (!hasFeatureLabel)
                {
                    return Ignore("ticket is not a feature");
                }
                if (QaCommand.IsGo(body))
                {
                    return new RoutingDecision(RoutingAction.ConfirmQa, "qa go on a ticket",
                        Qa: new QaRequest(owner, repo, issueNumber, false, null, commentAuthor));
                }
                if (QaCommand.Parse(body) is { } qa)
                {
                    // Only the factory's feature and bug tickets have a pull request to test (not an incident or a plan).
                    return kind is WorkIds.Feature or WorkIds.Bug
                        ? new RoutingDecision(RoutingAction.RunQa, "qa command on a ticket",
                            Qa: new QaRequest(owner, repo, issueNumber, false, qa.Suite, commentAuthor, qa.Mode, qa.Note))
                        : Ignore("qa needs a feature or bug ticket");
                }
                if (UpdateCommand.IsMatch(body))
                {
                    // The ticket's pull request is looked up when the update starts; only a feature or bug ticket has one.
                    return kind is WorkIds.Feature or WorkIds.Bug
                        ? new RoutingDecision(RoutingAction.UpdatePullRequest, "update command on a ticket",
                            Update: new UpdateRequest(owner, repo, issueNumber, false, commentAuthor))
                        : Ignore("update needs a feature or bug ticket");
                }
                // `/retry` starts the ticket's run again, like closing and reopening it, without touching the ticket.
                if (RetryCommand().Match(body.Replace("\r", "")) is { Success: true } retry)
                {
                    // `/retry` carries on from the stages an earlier run finished; `/retry restart` starts from the beginning like reopening the ticket.
                    var resume = !retry.Groups["restart"].Success;
                    return isOpen
                        ? new RoutingDecision(RoutingAction.RetryFeature, "retry", Restart: true, Feature: TicketRequest(input, kind!) with { Resume = resume })
                        : Ignore("ticket is closed");
                }
                return new RoutingDecision(
                    RoutingAction.ReplyToFeature,
                    "human comment",
                    Feature: new WorkRequest(owner, repo, issueNumber, "", "", "", Kind: kind!),
                    Reply: new HumanReply(commentAuthor, body));

            default:
                return Ignore($"unhandled {input.EventName}/{input.Action}");
        }
    }

    /// <summary>The ticket as the workflows get it: its text and labels as they are now.</summary>
    private static WorkRequest TicketRequest(RepoEvent ticket, string kind) =>
        new(ticket.Owner, ticket.Repo, ticket.Number, ticket.Title, ticket.Body, ticket.Author, ticket.LabelNames.ToList(), kind);

    /// <summary>A line that is just <c>/retry</c> (or <c>/retry restart</c>) on a ticket: run the factory on it again.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*/retry(?<restart>[ \t]+restart)?[ \t]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex RetryCommand();

    /// <summary>A line starting with <c>/fix</c> (or <c>/revise</c>) on a pull request; what follows it says what to change.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*/(?:fix|revise)\b(?<rest>[^\r\n]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex FixCommand();

    /// <summary>
    /// A comment on a pull request does nothing, except one with the <c>/fix</c> command: that asks the factory to revise its own pull
    /// request. Whether the pull request is the factory's is checked when the revision starts, because the comment does not say.
    /// </summary>
    private static RoutingDecision DecidePullRequestComment(RepoEvent input)
    {
        var (owner, repo, pullNumber) = (input.Owner, input.Repo, input.Number);
        var body = input.CommentBody;
        var author = input.CommentAuthor;
        if (body.Contains(BotMarker, StringComparison.Ordinal))
        {
            return Ignore("comment written by the factory");
        }
        if (QaCommand.IsGo(body))
        {
            return new RoutingDecision(RoutingAction.ConfirmQa, "qa go on a pull request",
                Qa: new QaRequest(owner, repo, pullNumber, true, null, author));
        }
        if (QaCommand.Parse(body) is { } qa)
        {
            return new RoutingDecision(RoutingAction.RunQa, "qa command on a pull request",
                Qa: new QaRequest(owner, repo, pullNumber, true, qa.Suite, author, qa.Mode, qa.Note));
        }
        if (AskCommand.Parse(body) is { } question)
        {
            return new RoutingDecision(RoutingAction.AskQuestion, "ask command on a pull request",
                Ask: new AskRequest(owner, repo, pullNumber, true, author, question));
        }
        if (ReviewCommand.IsMatch(body))
        {
            return new RoutingDecision(RoutingAction.ReviewPullRequest, "review command on a pull request",
                Review: new PullReviewRequest(owner, repo, pullNumber, author));
        }
        if (UpdateCommand.IsMatch(body))
        {
            return new RoutingDecision(RoutingAction.UpdatePullRequest, "update command on a pull request",
                Update: new UpdateRequest(owner, repo, pullNumber, true, author));
        }
        var match = FixCommand().Match(body);
        if (!match.Success)
        {
            return Ignore("pull request");
        }
        var instruction = (match.Groups["rest"].Value + body[(match.Index + match.Length)..]).Trim();
        return new RoutingDecision(
            RoutingAction.RevisePullRequest, "fix command on a pull request",
            Revision: new RevisionRequest(owner, repo, pullNumber, RevisionRequest.ByCommand, instruction, author));
    }

    /// <summary>A review that asks for changes on one of the factory's own branches starts a revision.</summary>
    private static RoutingDecision DecideReview(RepoEvent input)
    {
        if (input.HeadRef is null || !RevisionRules.FactoryBranch().IsMatch(input.HeadRef))
        {
            return Ignore("not a factory pull request");
        }
        if (input.PullState is { } state && state != "open")
        {
            return Ignore("pull request is not open");
        }
        var content = input.ReviewBody;
        if (content.Contains(BotMarker, StringComparison.Ordinal))
        {
            return Ignore("review written by the factory");
        }
        return new RoutingDecision(
            RoutingAction.RevisePullRequest, "review requested changes",
            Revision: new RevisionRequest(input.Owner, input.Repo, input.Number, RevisionRequest.ByReview, content.Trim(), input.Actor));
    }

    private static RoutingDecision Ignore(string reason) => new(RoutingAction.Ignore, reason);
}
