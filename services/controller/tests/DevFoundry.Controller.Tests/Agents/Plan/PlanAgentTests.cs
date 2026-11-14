using DevFoundry.Controller.Tests.Scenarios;

namespace DevFoundry.Controller.Tests;

public sealed class PlanParserTests
{
    [Fact]
    public void A_good_plan_is_read_and_the_tickets_that_wait_for_nothing_are_ready()
    {
        Assert.True(PlanParser.TryParse("Here:\n" + Answers.Plan(), out var plan, out var error), error);

        Assert.Equal(3, plan.Tickets.Count);
        Assert.Equal([1], plan.Tickets[2].DependsOn);
        Assert.Equal([1, 2], plan.Ready);
    }

    [Fact]
    public void A_plan_that_passes_without_a_ticket_is_refused_with_advice()
    {
        Assert.False(PlanParser.TryParse(Answers.Plan(tickets: 0), out _, out var error));
        Assert.Contains("needs at least one entry", error);
    }

    [Fact]
    public void More_than_eight_tickets_are_refused()
    {
        Assert.False(PlanParser.TryParse(Answers.Plan(tickets: 9), out _, out var error));
        Assert.Contains("at most 8", error);
    }

    [Fact]
    public void A_question_is_required_when_the_planner_needs_clarification()
    {
        Assert.True(PlanParser.TryParse(Answers.Plan("needs_clarification"), out var plan, out var error), error);
        Assert.Single(plan.Questions);
        Assert.False(PlanParser.TryParse(Answers.Plan("needs_clarification", questions: []), out _, out error));
        Assert.Contains("questions", error);
    }

    [Theory]
    [InlineData("[1]", "[]")] // the first ticket cannot wait for anything
    [InlineData("[]", "[2]")] // nor for itself
    [InlineData("[]", "[3]")] // nor for a later one
    [InlineData("[]", "[0]")] // nor for a ticket that does not exist
    [InlineData("[]", "[1, 1]")] // nor name one twice
    public void A_ticket_may_only_wait_for_different_earlier_tickets(string first, string second)
    {
        Assert.False(PlanParser.TryParse(Plan(first, second), out _, out var error));
        Assert.Contains("\"depends_on\"", error);
    }

    [Fact]
    public void A_later_ticket_may_wait_for_an_earlier_one()
    {
        Assert.True(PlanParser.TryParse(Plan("[]", "[1]"), out var plan, out var error), error);
        Assert.Equal([1], plan.Tickets[1].DependsOn);
    }

    [Fact]
    public void A_ticket_without_criteria_or_with_a_long_title_is_refused()
    {
        var none = Answers.Plan().Replace("\"acceptance_criteria\":[\"Criterion A of part 1\",\"Criterion B of part 1\"]", "\"acceptance_criteria\":[]");
        Assert.False(PlanParser.TryParse(none, out _, out var error));
        Assert.Contains("\"acceptance_criteria\" must list 1 to 8", error);

        var longTitle = Answers.Plan().Replace("\"Part 1\"", $"\"{new string('x', 121)}\"");
        Assert.False(PlanParser.TryParse(longTitle, out _, out error));
        Assert.Contains("\"title\" of at most 120", error);
    }

    private static string Plan(string first, string second) =>
        $$"""
        {"verdict":"pass","summary":"s","questions":[],"tickets":[
          {"title":"A","summary":"a","acceptance_criteria":["x"],"depends_on":{{first}}},
          {"title":"B","summary":"b","acceptance_criteria":["y"],"depends_on":{{second}}}]}
        """;
}

public sealed class PlanCommandTests
{
    [Fact]
    public void A_plain_approve_names_no_ticket() =>
        Assert.Empty(PlanCommands.Parse("Looks good.\n/approve")!.Numbers);

    [Theory]
    [InlineData("/approve 2 3", new[] { 2, 3 })]
    [InlineData("/approve 2, 3", new[] { 2, 3 })]
    [InlineData("/approve #2 #3", new[] { 2, 3 })]
    [InlineData("/approve 3\n/approve 3 1", new[] { 3, 1 })]
    [InlineData("  /APPROVE 4  ", new[] { 4 })]
    public void Named_tickets_are_read_once_in_the_order_written(string body, int[] expected) =>
        Assert.Equal(expected, PlanCommands.Parse(body)!.Numbers);

    [Theory]
    [InlineData("/approve please")]
    [InlineData("/approve 2 but rename it")]
    [InlineData("please /approve 2")]
    [InlineData("/approved")]
    [InlineData("I do not approve")]
    [InlineData("")]
    public void Anything_else_is_not_an_approval(string body) =>
        Assert.Null(PlanCommands.Parse(body));
}

public sealed class PlanCommentTests
{
    private static TicketPlan Plan()
    {
        Assert.True(PlanParser.TryParse(Answers.Plan(), out var plan, out var error), error);
        return plan;
    }

    [Fact]
    public void The_proposal_lists_the_tickets_and_how_to_approve_them()
    {
        var text = PlanComments.Proposal(Plan());

        Assert.Contains("**Plan: proposed tickets**", text);
        Assert.Contains("**3. Part 3**", text);
        Assert.Contains("Waits for: 1", text);
        Assert.Contains("`/approve` to create the tickets that wait for nothing (1, 2)", text);
        Assert.Contains("Nothing is created until a person approves", text);
    }

    [Fact]
    public void A_new_ticket_names_what_it_waits_for_as_a_ticket_when_there_is_one_and_as_a_plan_item_when_not()
    {
        var plan = Plan();
        var request = new WorkRequest("acme", "shop", 5, "Big", "", "alice", Kind: WorkIds.Plan);

        var (_, before) = PlanComments.NewTicket(request, plan, 3, new Dictionary<int, CreatedTicket>(), "bob");
        var (title, after) = PlanComments.NewTicket(request, plan, 3, new Dictionary<int, CreatedTicket> { [1] = new(1, 91, "http://x/91") }, "bob");

        Assert.Contains("- Part 1 (plan item 1, no ticket yet)", before);
        Assert.Contains("- #91: Part 1", after);
        Assert.Equal("Part 3", title);
        Assert.Contains("from plan #5, at the request of bob. Item 3 of 3.", after);
        Assert.Contains("- Criterion A of part 3", after);
    }

    [Fact]
    public void Text_from_the_model_cannot_mention_people_or_break_the_formatting()
    {
        Assert.True(PlanParser.TryParse(Answers.Plan().Replace("Part 1", "Ping @admin `x`"), out var plan, out _));
        var request = new WorkRequest("acme", "shop", 5, "Big", "", "alice", Kind: WorkIds.Plan);

        var (title, body) = PlanComments.NewTicket(request, plan, 1, new Dictionary<int, CreatedTicket>(), "bob");

        Assert.DoesNotContain("@admin", title);
        Assert.DoesNotContain("`", title);
        Assert.DoesNotContain("@admin", body);
    }
}
