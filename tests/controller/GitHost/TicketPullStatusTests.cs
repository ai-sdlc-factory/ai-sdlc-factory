namespace AiSdlcFactory.Controller.Tests;

public sealed class TicketPullStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static PullSummary Pull(int number, string state, bool merged = false, string head = "factory/issue-7", string author = "factory-bot") =>
        new(number, state, merged, head, author, Now, merged ? Now : null);

    [Fact]
    public void A_ticket_with_no_pull_request_of_the_factory_has_none()
    {
        var pulls = new[] { Pull(3, "open", head: "factory/issue-8"), Pull(4, "open", author: "alice"), Pull(5, "open", head: "alice/issue-7") };
        Assert.Equal(TicketPullStatus.NoPull, TicketPullStatus.Of(7, pulls, "factory-bot"));
        Assert.Equal(TicketPullStatus.NoPull, TicketPullStatus.Of(7, [], "factory-bot"));
    }

    [Theory]
    [InlineData("open", false, TicketPullState.Open)]
    [InlineData("closed", true, TicketPullState.Merged)]
    [InlineData("closed", false, TicketPullState.Closed)]
    public void The_state_is_open_merged_or_closed_without_merging(string state, bool merged, TicketPullState expected) =>
        Assert.Equal(new TicketPullStatus(expected, 9), TicketPullStatus.Of(7, [Pull(9, state, merged)], "Factory-Bot"));

    [Fact]
    public void The_newest_pull_request_decides_and_a_second_branch_of_the_ticket_counts()
    {
        var pulls = new[] { Pull(9, "closed", merged: true), Pull(12, "open", head: "factory/issue-7-2"), Pull(10, "closed") };
        Assert.Equal(new TicketPullStatus(TicketPullState.Open, 12), TicketPullStatus.Of(7, pulls, "factory-bot"));
    }

    [Fact]
    public void Only_an_open_pull_request_of_the_factory_in_its_own_repository_is_a_target_for_the_hint()
    {
        PullRequestDetails Details(string state = "open", bool merged = false, string head = "factory/issue-7", string author = "factory-bot", string headRepo = "acme/shop") =>
            new(7, "t", "b", state, merged, head, "sha", headRepo, "acme/shop", "main", author);

        Assert.True(RevisionRules.IsOpenFactoryPull(Details(), "factory-bot"));
        Assert.False(RevisionRules.IsOpenFactoryPull(Details(state: "closed"), "factory-bot"));
        Assert.False(RevisionRules.IsOpenFactoryPull(Details(state: "closed", merged: true), "factory-bot"));
        Assert.False(RevisionRules.IsOpenFactoryPull(Details(author: "alice"), "factory-bot"));
        Assert.False(RevisionRules.IsOpenFactoryPull(Details(head: "alice/goodbye"), "factory-bot"));
        Assert.False(RevisionRules.IsOpenFactoryPull(Details(headRepo: "fork/shop"), "factory-bot"));
    }
}
