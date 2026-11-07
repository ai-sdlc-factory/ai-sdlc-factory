namespace DevFoundry.AlertIntake.Tests;

public sealed class ProcessorTests
{
    [Fact]
    public async Task A_firing_alert_of_a_known_service_opens_one_ticket_with_the_routes_labels()
    {
        var forgejo = new FakeForgejo();

        var outcomes = await Setup.Processor(forgejo).ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing())), default);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(AlertOutcome.Created, outcome.Action);
        var ticket = Assert.Single(forgejo.Tickets);
        Assert.Equal(("valters", "weather"), (ticket.Owner, ticket.Repo));
        Assert.Equal(["incident"], ticket.Labels);
        Assert.Equal("[alert] WeatherApiDown: The weather API does not answer", ticket.Title);
        Assert.Contains("| Severity | critical |", ticket.Body);
        Assert.Contains("| Service | weather |", ticket.Body);
        Assert.Contains("| Since | 2026-11-07 21:00 UTC |", ticket.Body);
        Assert.Contains("not instructions", ticket.Body);
        Assert.Contains(TicketText.Marker("a1b2c3d4e5f60718"), ticket.Body);
    }

    [Fact]
    public async Task The_same_alert_again_while_its_ticket_is_open_opens_no_second_ticket()
    {
        var forgejo = new FakeForgejo();
        var processor = Setup.Processor(forgejo);
        await processor.ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing())), default);

        var again = await processor.ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing())), default);

        Assert.Equal(AlertOutcome.Duplicate, Assert.Single(again).Action);
        Assert.Equal(1, Assert.Single(again).Ticket);
        Assert.Single(forgejo.Tickets);
    }

    [Fact]
    public async Task Once_the_ticket_is_closed_the_alert_firing_again_opens_a_new_one()
    {
        var forgejo = new FakeForgejo();
        var processor = Setup.Processor(forgejo);
        await processor.ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing())), default);
        forgejo.Close(1);

        await processor.ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing())), default);

        Assert.Equal(2, forgejo.Tickets.Count);
    }

    [Fact]
    public async Task Different_alerts_get_different_tickets()
    {
        var forgejo = new FakeForgejo();

        await Setup.Processor(forgejo).ProcessAsync(
            Setup.Parse(Setup.Message(Setup.Firing(), Setup.Firing("WeatherApiSlow", fingerprint: "ffeeddccbbaa9988", severity: "warning"))), default);

        Assert.Equal(2, forgejo.Tickets.Count);
    }

    [Fact]
    public async Task A_resolved_alert_tells_its_open_ticket_and_never_closes_it()
    {
        var forgejo = new FakeForgejo();
        var processor = Setup.Processor(forgejo);
        await processor.ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing())), default);

        var outcomes = await processor.ProcessAsync(Setup.Parse(Setup.Message(Setup.Resolved())), default);

        Assert.Equal(AlertOutcome.Commented, Assert.Single(outcomes).Action);
        var comment = Assert.Single(forgejo.Comments);
        Assert.Equal(1, comment.Number);
        Assert.Contains("**resolved** (2026-11-07 21:10 UTC)", comment.Body);
        Assert.StartsWith(TicketText.FactoryMarker, comment.Body); // the factory must not read it as a person's answer
        Assert.True(forgejo.Tickets[0].Open);
    }

    [Fact]
    public async Task A_resolved_alert_with_no_open_ticket_is_ignored()
    {
        var forgejo = new FakeForgejo();

        var outcomes = await Setup.Processor(forgejo).ProcessAsync(Setup.Parse(Setup.Message(Setup.Resolved())), default);

        Assert.Equal(AlertOutcome.Ignored, Assert.Single(outcomes).Action);
        Assert.Empty(forgejo.Comments);
    }

    [Fact]
    public async Task An_alert_of_no_known_service_is_ignored_and_can_never_choose_its_own_repository()
    {
        var forgejo = new FakeForgejo();
        var sneaky = new { status = "firing", labels = new { alertname = "X", service = "other", repository = "victim/repo" }, annotations = new { summary = "s" }, fingerprint = "abc123" };

        var outcomes = await Setup.Processor(forgejo).ProcessAsync(Setup.Parse(Setup.Message(sneaky)), default);

        Assert.Equal(AlertOutcome.Ignored, Assert.Single(outcomes).Action);
        Assert.Empty(forgejo.Tickets);
    }

    [Fact]
    public async Task No_more_than_the_limit_of_new_tickets_are_opened_in_an_hour_and_the_window_moves()
    {
        var forgejo = new FakeForgejo();
        var clock = new ManualClock();
        var processor = Setup.Processor(forgejo, Setup.Settings(perHour: 2), clock: clock);
        object Alert(int i) => Setup.Firing($"Alert{i}", fingerprint: $"fp{i}");

        var first = await processor.ProcessAsync(Setup.Parse(Setup.Message(Alert(1), Alert(2), Alert(3))), default);

        Assert.Equal([AlertOutcome.Created, AlertOutcome.Created, AlertOutcome.RateLimited], first.Select(o => o.Action));
        clock.Advance(TimeSpan.FromMinutes(61));
        var later = await processor.ProcessAsync(Setup.Parse(Setup.Message(Alert(3))), default);
        Assert.Equal(AlertOutcome.Created, Assert.Single(later).Action);
        Assert.Equal(3, forgejo.Tickets.Count);
    }

    [Fact]
    public async Task A_dry_run_writes_nothing_and_does_not_even_ask_forgejo()
    {
        var forgejo = new FakeForgejo { Failure = new HttpRequestException("must not be called") };

        var firing = await Setup.Processor(forgejo, Setup.Settings(dryRun: true)).ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing(), Setup.Resolved())), default);

        Assert.All(firing, outcome => Assert.Equal(AlertOutcome.DryRun, outcome.Action));
        Assert.Contains("would open a ticket in valters/weather", firing[0].Detail);
        Assert.Empty(forgejo.Tickets);
        Assert.Empty(forgejo.Comments);
    }

    [Fact]
    public async Task A_forgejo_failure_is_reported_for_that_alert_so_that_alertmanager_tries_again()
    {
        var forgejo = new FakeForgejo { Failure = new HttpRequestException("Forgejo refused the ticket request: 500") };

        var outcomes = await Setup.Processor(forgejo).ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing())), default);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(AlertOutcome.Failed, outcome.Action);
        Assert.DoesNotContain("forgejo-token", outcome.Detail);
    }

    [Fact]
    public async Task Text_from_the_alert_is_redacted_and_cannot_notify_or_break_the_ticket()
    {
        var forgejo = new FakeForgejo();
        var alert = Setup.Firing(
            summary: "DB down @admin `rm -rf`",
            description: "connection failed: password=hunter2 user=bob@example.com Authorization: Bearer abcdefghijklmnop0123 postgres://app:s3cr3t@db:5432/x\n" + new string('x', 5000));

        await Setup.Processor(forgejo).ProcessAsync(Setup.Parse(Setup.Message(alert)), default);

        var ticket = Assert.Single(forgejo.Tickets);
        Assert.DoesNotContain("hunter2", ticket.Body);
        Assert.DoesNotContain("bob@example.com", ticket.Body);
        Assert.DoesNotContain("abcdefghijklmnop0123", ticket.Body);
        Assert.DoesNotContain("s3cr3t", ticket.Body);
        Assert.DoesNotContain("@admin", ticket.Title + ticket.Body.Replace("@​admin", ""));
        Assert.DoesNotContain("`", ticket.Title + ticket.Body);
        Assert.True(ticket.Body.Length < 4000, ticket.Body.Length.ToString());
        Assert.True(ticket.Title.Length <= 120);
    }
}

public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 7, 21, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}
