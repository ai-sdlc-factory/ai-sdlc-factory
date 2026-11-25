namespace AiSdlcFactory.Controller.Tests;

/// <summary>The go-ahead for the acceptance criteria: a line that is only <c>/approve</c>, and nothing that merely mentions it.</summary>
public sealed class CriteriaApprovalTests
{
    [Theory]
    [InlineData("/approve")]
    [InlineData("  /APPROVE  ")]
    [InlineData("Looks right.\n/approve\nThanks")]
    [InlineData("/approve\r\n")]
    public void A_line_that_is_only_approve_is_an_approval(string body) => Assert.True(CriteriaApproval.IsApproval(body));

    [Theory]
    [InlineData("/approve please")]
    [InlineData("/approve 2 3")]
    [InlineData("I would /approve this if it handled 404s")]
    [InlineData("approve")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_an_approval(string? body) => Assert.False(CriteriaApproval.IsApproval(body));
}
