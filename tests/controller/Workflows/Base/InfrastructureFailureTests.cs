using Temporalio.Api.Enums.V1;
using Temporalio.Api.Failure.V1;
using Temporalio.Converters;
using Temporalio.Exceptions;

namespace AiSdlcFactory.Controller.Tests;

public sealed class InfrastructureFailureTests
{
    /// <summary>The SDK builds these itself from the server's answer; its constructor is for subclasses.</summary>
    private sealed class Timeout(TimeoutType type) : TimeoutFailureException(
        new Failure { Message = "timeout", TimeoutFailureInfo = new TimeoutFailureInfo { TimeoutType = type } }, null, DataConverter.Default.PayloadConverter);

    [Fact]
    public void A_docker_error_from_the_launcher_is_the_factorys_problem()
    {
        var cause = new ApplicationFailureException("starting the sandbox failed: no space left", errorType: "Docker");

        Assert.True(InfrastructureFailure.IsCause(cause));
        Assert.Equal("starting the sandbox failed: no space left", InfrastructureFailure.DescribeCause(cause, "x"));
    }

    [Theory]
    [InlineData(TimeoutType.Heartbeat, "stopped reporting progress")]
    [InlineData(TimeoutType.ScheduleToStart, "did not pick the run up")]
    [InlineData(TimeoutType.StartToClose, "did not finish in time")]
    public void A_timeout_of_a_sandbox_run_is_the_factorys_problem_and_says_which(TimeoutType type, string words)
    {
        var cause = new Timeout(type);

        Assert.True(InfrastructureFailure.IsCause(cause));
        Assert.Contains(words, InfrastructureFailure.DescribeCause(cause, "x"));
    }

    [Theory]
    [InlineData("SandboxPolicy")]
    [InlineData("NotSignedIn")]
    [InlineData(null)]
    public void A_rejected_request_a_missing_login_or_any_other_error_is_not(string? type)
    {
        var cause = new ApplicationFailureException("nope", errorType: type, nonRetryable: true);

        Assert.False(InfrastructureFailure.IsCause(cause));
        Assert.Equal("nope", InfrastructureFailure.DescribeCause(cause, "x"));
    }

    [Fact]
    public void A_failure_without_a_cause_is_not_infrastructure()
    {
        Assert.False(InfrastructureFailure.IsCause(null));
        Assert.Equal("fallback", InfrastructureFailure.DescribeCause(null, "fallback"));
    }
}
