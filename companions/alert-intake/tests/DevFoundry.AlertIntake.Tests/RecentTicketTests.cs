namespace DevFoundry.AlertIntake.Tests;

/// <summary>A host that does not list a ticket for a few seconds after it was created must not make the intake open a second one.</summary>
public sealed class RecentTicketTests
{
    private static AlertMessage Message(string name, string fingerprint, string status = "firing") =>
        Setup.Parse(Setup.Message(status == "firing" ? Setup.Firing(name, fingerprint: fingerprint) : Setup.Resolved(name, fingerprint: fingerprint)));

    [Fact]
    public async Task The_same_alert_a_moment_later_is_a_duplicate_even_when_the_host_does_not_list_its_ticket_yet()
    {
        var forgejo = new FakeForgejo { ListingLags = true };
        var processor = Setup.Processor(forgejo, settings: Setup.Settings(group: 15));

        var first = await processor.ProcessAsync(Message("A", "aaaa1111"), default);
        var again = await processor.ProcessAsync(Message("A", "aaaa1111"), default);

        Assert.Equal(AlertOutcome.Created, first[0].Action);
        Assert.Equal(AlertOutcome.Duplicate, again[0].Action);
        Assert.Equal(first[0].Ticket, again[0].Ticket);
        Assert.Single(forgejo.Tickets);
    }

    [Fact]
    public async Task Another_alert_of_the_same_route_a_moment_later_is_grouped_into_the_new_ticket()
    {
        var forgejo = new FakeForgejo { ListingLags = true };
        var processor = Setup.Processor(forgejo, settings: Setup.Settings(group: 15));

        var first = await processor.ProcessAsync(Message("A", "aaaa1111"), default);
        var second = await processor.ProcessAsync(Message("B", "bbbb2222"), default);

        Assert.Equal(AlertOutcome.Grouped, second[0].Action);
        Assert.Equal(first[0].Ticket, second[0].Ticket);
        Assert.Single(forgejo.Tickets);
        Assert.Single(forgejo.Comments);
    }

    [Fact]
    public async Task A_resolved_alert_a_moment_later_tells_the_new_ticket()
    {
        var forgejo = new FakeForgejo { ListingLags = true };
        var processor = Setup.Processor(forgejo);

        var first = await processor.ProcessAsync(Message("A", "aaaa1111"), default);
        var resolved = await processor.ProcessAsync(Message("A", "aaaa1111", "resolved"), default);

        Assert.Equal(AlertOutcome.Commented, resolved[0].Action);
        Assert.Equal(first[0].Ticket, resolved[0].Ticket);
    }

    [Fact]
    public async Task After_two_minutes_only_the_host_decides_so_a_closed_ticket_is_not_commented_on_or_counted()
    {
        var clock = new ManualClock();
        var forgejo = new FakeForgejo { Clock = clock };
        var processor = Setup.Processor(forgejo, clock: clock);

        await processor.ProcessAsync(Message("A", "aaaa1111"), default);
        forgejo.Tickets[0] = forgejo.Tickets[0] with { Open = false };
        clock.Advance(RecentTickets.Remember + TimeSpan.FromSeconds(1));
        var again = await processor.ProcessAsync(Message("A", "aaaa1111"), default);

        Assert.Equal(AlertOutcome.Created, again[0].Action);
        Assert.Equal(2, forgejo.Tickets.Count);
    }

    [Fact]
    public async Task A_dry_run_remembers_nothing()
    {
        var forgejo = new FakeForgejo();
        var processor = Setup.Processor(forgejo, settings: Setup.Settings(dryRun: true));

        await processor.ProcessAsync(Message("A", "aaaa1111"), default);

        Assert.Empty(forgejo.Tickets);
    }
}
