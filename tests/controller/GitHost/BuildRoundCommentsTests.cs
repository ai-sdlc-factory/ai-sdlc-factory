namespace AiSdlcFactory.Controller.Tests;

public sealed class BuildRoundCommentsTests
{
    [Fact]
    public void The_first_round_says_the_developer_is_working()
    {
        var text = TicketComments.BuildRoundStarted(1, 3, null);

        Assert.Equal("**Build round 1 of 3:** the developer is working on the ticket.", text);
    }

    [Fact]
    public void After_a_review_the_round_lists_the_findings_it_is_fixing()
    {
        var feedback = ReviewFeedback.From(
            [("reviewer", new ReviewReport("changes_needed", "s", [new ReviewFinding("major", "src/A.cs", "Check the id first.")])),
             ("security", new ReviewReport("changes_needed", "s", [new ReviewFinding("blocker", null, "Do not log the key.")]))], null)!;

        var text = TicketComments.BuildRoundStarted(2, 3, feedback);

        Assert.StartsWith("**Build round 2 of 3:** the developer is fixing 2 findings from the review.", text);
        Assert.Contains("- [reviewer, major] src/A.cs: Check the id first.", text);
        Assert.Contains("- [security, blocker] Do not log the key.", text);
    }

    [Fact]
    public void A_long_list_is_cut_and_text_in_it_cannot_notify_people_or_break_the_comment()
    {
        var findings = string.Join('\n', Enumerable.Range(1, 9).Select(i => $"[reviewer, major] finding {i} @admin `x`"));
        var text = TicketComments.BuildRoundStarted(2, 3, ReviewFeedback.Header + "\n" + findings);

        Assert.Contains("fixing 9 findings", text);
        Assert.Contains("- ... and 3 more", text);
        Assert.DoesNotContain("finding 7", text);
        Assert.DoesNotContain("@admin", text.Replace("@​admin", ""));
        Assert.DoesNotContain("`", text);
    }

    [Fact]
    public void After_a_failed_check_the_round_points_back_to_the_report_instead_of_pasting_output()
    {
        var text = TicketComments.BuildRoundStarted(2, 3, "Check \"test\" failed with exit code 1.\nFAILED test_x");

        Assert.Equal("**Build round 2 of 3:** the developer is fixing the problem reported above.", text);
    }

    [Fact]
    public void What_the_developer_says_it_did_is_shown_with_a_reminder_that_it_is_unverified()
    {
        var text = TicketComments.DeveloperFinished(2, new DeveloperReport("done", "Moved the city check before the cache.", ["Added a test for unknown ids.", "Left the TTL as is."]));

        Assert.Contains("**Build round 2: the developer finished**", text);
        Assert.Contains("Moved the city check before the cache.", text);
        Assert.Contains("- Added a test for unknown ids.", text);
        Assert.EndsWith("The checks and the review decide whether this is right.", text);
    }

    [Fact]
    public void A_very_long_summary_is_cut()
    {
        var text = TicketComments.DeveloperFinished(1, new DeveloperReport("done", new string('x', 5000), []));

        Assert.True(text.Length < 1100, text.Length.ToString());
    }
}
