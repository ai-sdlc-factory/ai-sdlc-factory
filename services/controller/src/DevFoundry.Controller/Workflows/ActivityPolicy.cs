using Temporalio.Common;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

/// <summary>
/// How long and how often the workflows let an activity run. Kept in one place so a timeout is read, and changed, once, instead of in the
/// thirty places that call an activity.
/// </summary>
internal static class ActivityPolicy
{
    /// <summary>
    /// How long the launcher may go without a sign of life before a sandbox run is declared dead. The Temporal SDK sends heartbeats
    /// at most about every 80% of this, so a short value leaves little margin: a wall clock that steps by several seconds (seen under
    /// WSL2) made 30 seconds time out a healthy run.
    /// </summary>
    public static readonly TimeSpan SandboxHeartbeat = TimeSpan.FromMinutes(2);

    /// <summary>
    /// An activity of the controller itself (git, files, the git host). <paramref name="attempts"/> bounds the tries; without it Temporal's default
    /// retry policy applies (keep trying with growing pauses).
    /// </summary>
    public static ActivityOptions Local(TimeSpan timeout, int? attempts = null) => new()
    {
        StartToCloseTimeout = timeout,
        RetryPolicy = attempts is { } tries ? new RetryPolicy { MaximumAttempts = tries } : null,
    };

    /// <summary>
    /// A run in a sandbox, on the launcher's queue. It reports that it is alive (the heartbeat) and is never retried blindly: a model run
    /// can use plan usage and a command may already have changed files, so <paramref name="attempts"/> is small and deliberate.
    /// </summary>
    public static ActivityOptions Sandbox(TimeSpan timeout, int attempts, TimeSpan? heartbeat = null) => new()
    {
        TaskQueue = SandboxNames.DefaultTaskQueue,
        StartToCloseTimeout = timeout,
        HeartbeatTimeout = heartbeat ?? SandboxHeartbeat,
        RetryPolicy = new RetryPolicy { MaximumAttempts = attempts },
    };
}
