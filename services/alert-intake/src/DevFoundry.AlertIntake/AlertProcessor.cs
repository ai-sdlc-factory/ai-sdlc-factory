namespace DevFoundry.AlertIntake;

/// <summary>What happened to one alert.</summary>
public sealed record AlertOutcome(string Alert, string Action, string? Detail = null, int? Ticket = null)
{
    public const string Created = "created";
    public const string Duplicate = "duplicate";
    public const string Commented = "commented";
    public const string Grouped = "grouped";
    public const string RateLimited = "rate_limited";
    public const string Ignored = "ignored";
    public const string DryRun = "dry_run";
    public const string Failed = "failed";
}

/// <summary>
/// Decides, with fixed rules and no AI, what an Alertmanager notification does: a firing alert of a known service opens one ticket (never a second one
/// while that ticket is open), a resolved alert adds a comment to its ticket, and everything else is ignored or limited.
/// </summary>
public sealed class AlertProcessor(IntakeSettings settings, RouteTable routes, IForgejo forgejo, ILogSource logs, RateLimiter limiter, IntakeMetrics metrics, ILogger<AlertProcessor> log, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> clock = now ?? (() => DateTimeOffset.UtcNow);

    public async Task<IReadOnlyList<AlertOutcome>> ProcessAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        var outcomes = new List<AlertOutcome>();
        foreach (var alert in message.Alerts!)
        {
            AlertOutcome outcome;
            try
            {
                outcome = await ProcessOneAsync(alert, cancellationToken);
            }
            catch (HttpRequestException error)
            {
                // Alertmanager retries a failed notification, so say so instead of losing the alert.
                log.LogWarning("Forgejo failed for alert {Alert}: {Error}", alert.Name, error.Message);
                outcome = new AlertOutcome(alert.Name, AlertOutcome.Failed, error.Message);
            }
            outcomes.Add(outcome);
            metrics.Outcome(outcome.Action);
        }
        return outcomes;
    }

    private async Task<AlertOutcome> ProcessOneAsync(Alert alert, CancellationToken cancellationToken)
    {
        var name = alert.Name;
        if (routes.Find(alert.LabelMap) is not { } route)
        {
            log.LogInformation("Alert {Alert} matches no route, ignored", name);
            return new AlertOutcome(name, AlertOutcome.Ignored, "no route matches this alert's labels");
        }

        var key = alert.Key;
        if (alert.Status == Alert.Resolved)
        {
            return await ResolveAsync(alert, route, key, cancellationToken);
        }

        var title = TicketText.Title(alert);
        if (settings.DryRun)
        {
            log.LogInformation("Dry run: would open a ticket in {Owner}/{Repo} titled {Title}", route.Owner, route.Repo, title);
            return new AlertOutcome(name, AlertOutcome.DryRun, $"would open a ticket in {route.Owner}/{route.Repo}: {title}");
        }

        if (await forgejo.FindOpenTicketAsync(route.Owner, route.Repo, key, cancellationToken) is { } existing)
        {
            return new AlertOutcome(name, AlertOutcome.Duplicate, "an open ticket already exists", existing);
        }
        // One outage often raises several alerts. An alert of the same route while that route's ticket is recent goes into it as a comment, so each outage is one ticket and one run.
        var group = TicketText.GroupMarker(route);
        if (settings.GroupMinutes > 0 && await forgejo.FindRecentTicketAsync(route.Owner, route.Repo, group, clock().AddMinutes(-settings.GroupMinutes), cancellationToken) is { } grouped)
        {
            await forgejo.CommentAsync(route.Owner, route.Repo, grouped, TicketText.GroupedComment(alert, alert.LabelMap.GetValueOrDefault("severity")), cancellationToken);
            // Its own marker goes into the ticket, so a repeat of this alert is a duplicate and its resolution is told to this ticket.
            await forgejo.AddMarkerAsync(route.Owner, route.Repo, grouped, TicketText.Marker(key), cancellationToken);
            log.LogInformation("Alert {Alert} added to ticket {Number} in {Owner}/{Repo}", name, grouped, route.Owner, route.Repo);
            return new AlertOutcome(name, AlertOutcome.Grouped, "added to the open ticket of the same route", grouped);
        }
        if (!limiter.TryAcquire())
        {
            log.LogWarning("Alert {Alert} not turned into a ticket: the limit of {Limit} per hour is reached", name, settings.MaxTicketsPerHour);
            return new AlertOutcome(name, AlertOutcome.RateLimited, $"at most {settings.MaxTicketsPerHour} new tickets per hour");
        }

        var severity = alert.LabelMap.GetValueOrDefault("severity");
        var evidence = await GatherEvidenceAsync(route, cancellationToken);
        var ticket = await forgejo.CreateTicketAsync(route.Owner, route.Repo, title, TicketText.Body(alert, severity, evidence, group), route.Labels, cancellationToken);
        log.LogInformation("Opened ticket {Number} in {Owner}/{Repo} for alert {Alert}", ticket.Number, route.Owner, route.Repo, name);
        return new AlertOutcome(name, AlertOutcome.Created, null, ticket.Number);
    }

    private static readonly TimeSpan EvidenceWindow = TimeSpan.FromMinutes(15);

    /// <summary>The operator's log queries for the route. A failing log source never stops the ticket: it gets an empty block that says so.</summary>
    private async Task<IReadOnlyList<TicketText.EvidenceBlock>> GatherEvidenceAsync(Route route, CancellationToken cancellationToken)
    {
        var blocks = new List<TicketText.EvidenceBlock>();
        foreach (var query in route.Evidence ?? [])
        {
            IReadOnlyList<string> lines;
            try
            {
                lines = await logs.RecentLinesAsync(query.Query, EvidenceWindow, TicketText.MaxEvidenceLines, cancellationToken);
            }
            catch (HttpRequestException error)
            {
                log.LogWarning("Log evidence {Title} could not be read: {Error}", query.Title, error.Message);
                lines = [];
            }
            blocks.Add(new TicketText.EvidenceBlock(query.Title, lines));
        }
        return blocks;
    }

    private async Task<AlertOutcome> ResolveAsync(Alert alert, Route route, string key, CancellationToken cancellationToken)
    {
        if (settings.DryRun)
        {
            log.LogInformation("Dry run: would comment that alert {Alert} resolved in {Owner}/{Repo}", alert.Name, route.Owner, route.Repo);
            return new AlertOutcome(alert.Name, AlertOutcome.DryRun, "would comment that the alert resolved");
        }
        if (await forgejo.FindOpenTicketAsync(route.Owner, route.Repo, key, cancellationToken) is not { } number)
        {
            return new AlertOutcome(alert.Name, AlertOutcome.Ignored, "resolved, and no open ticket to tell");
        }
        await forgejo.CommentAsync(route.Owner, route.Repo, number, TicketText.ResolvedComment(alert), cancellationToken);
        return new AlertOutcome(alert.Name, AlertOutcome.Commented, null, number);
    }
}
