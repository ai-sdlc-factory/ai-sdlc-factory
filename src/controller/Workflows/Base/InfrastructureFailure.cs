using Temporalio.Api.Enums.V1;
using Temporalio.Exceptions;

namespace AiSdlcFactory.Controller;

/// <summary>
/// Tells a failure of the factory's own machinery from a problem with the ticket or with what an agent wrote. When a sandbox stops
/// reporting progress, the launcher is down, or Docker refuses to start a container, running the same thing again is likely to work;
/// when an agent's answer is unusable or a check fails, it is not. The ticket says which one it was, so a person knows whether to
/// change the ticket or just say <c>/retry</c>.
/// </summary>
public static class InfrastructureFailure
{
    /// <summary>The launcher's error type for a failure of Docker itself (not a rejected request, not a missing login).</summary>
    public const string DockerErrorType = "Docker";

    public static bool Is(ActivityFailureException error) => IsCause(error.InnerException);

    /// <summary>Whether this is what a failed activity was caused by when it was the factory's machinery.</summary>
    public static bool IsCause(Exception? cause) => cause switch
    {
        TimeoutFailureException => true,
        ApplicationFailureException { ErrorType: DockerErrorType } => true,
        _ => false,
    };

    /// <summary>What went wrong, in words for the ticket.</summary>
    public static string Describe(ActivityFailureException error) => DescribeCause(error.InnerException, error.Message);

    public static string DescribeCause(Exception? cause, string fallback) => cause switch
    {
        TimeoutFailureException { TimeoutType: TimeoutType.Heartbeat } =>
            "the sandbox stopped reporting progress (heartbeat timeout)",
        TimeoutFailureException { TimeoutType: TimeoutType.ScheduleToStart } =>
            "the sandbox launcher did not pick the run up (is it running?)",
        TimeoutFailureException => "the sandbox run did not finish in time",
        { } inner => inner.Message,
        _ => fallback,
    };
}
