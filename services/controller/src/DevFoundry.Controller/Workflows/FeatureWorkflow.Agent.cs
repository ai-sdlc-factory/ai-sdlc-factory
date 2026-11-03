using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public sealed partial class FeatureWorkflow
{
    private sealed record AgentOutcome<T>(T? Value, bool NotSignedIn, string? Problem) where T : class;

    /// <summary>
    /// Runs one agent role in a read-only sandbox with model access and parses its answer. An unusable
    /// answer gets one more try that says what was wrong; after that the caller asks a person.
    /// </summary>
    private async Task<AgentOutcome<T>> RunAgentAsync<T>(
        string role, string image, WorkspaceInfo workspace, Func<string?, string> prompt, Func<string, (T? Value, string? Error)> parse,
        bool writable = false, int timeoutSeconds = 900)
        where T : class
    {
        string? problem = null;
        for (var attempt = 1; attempt <= MaxAgentAttempts; attempt++)
        {
            SandboxRunResult result;
            var promptText = prompt(problem);
            var started = Workflow.UtcNow;
            try
            {
                result = await Workflow.ExecuteActivityAsync<SandboxRunResult>(
                    SandboxNames.ActivityName,
                    [new SandboxRunRequest(
                        Image: image,
                        Workspace: workspace.Name,
                        WorkspaceWritable: writable,
                        Command: ["opencode", "run", "--agent", role, "--format", "json", promptText],
                        TimeoutSeconds: timeoutSeconds,
                        NeedsModelAuth: true,
                        Network: NetworkFor(role),
                        Role: role)],
                    new ActivityOptions
                    {
                        TaskQueue = SandboxNames.DefaultTaskQueue,
                        StartToCloseTimeout = TimeSpan.FromSeconds(timeoutSeconds + 300),
                        HeartbeatTimeout = TimeSpan.FromSeconds(30),
                        // A model run can consume plan usage; the retry below is a deliberate, bounded second try.
                        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
                    });
            }
            catch (ActivityFailureException error)
            {
                var message = error.InnerException?.Message ?? error.Message;
                await RecordAsync(workspace, "agent", role, role, attempt, started, "failed", -1, false, promptText, "", message);
                return message.Contains("not signed in", StringComparison.OrdinalIgnoreCase)
                    ? new AgentOutcome<T>(null, true, null)
                    : new AgentOutcome<T>(null, false, message);
            }

            var spent = OpenCodeEvents.Usage(result.Stdout, result.OutputTruncated);
            if (result.ExitCode == 78)
            {
                await RecordAsync(workspace, "agent", role, role, attempt, started, "not signed in", result.ExitCode, false, promptText, result.Stderr, "OpenCode is not signed in");
                return new AgentOutcome<T>(null, true, null);
            }
            if (result.TimedOut)
            {
                problem = $"the {role} ran out of time";
                await RecordAsync(workspace, "agent", role, role, attempt, started, "failed", result.ExitCode, true, promptText, result.Stderr, problem, spent);
                continue;
            }
            if (result.ExitCode != 0)
            {
                problem = $"OpenCode exited with code {result.ExitCode}: {Short(result.Stderr)}";
                await RecordAsync(workspace, "agent", role, role, attempt, started, "failed", result.ExitCode, false, promptText, result.Stderr, problem, spent);
                continue;
            }

            var text = OpenCodeEvents.FinalText(result.Stdout);
            if (text is null)
            {
                problem = result.OutputTruncated ? "the output was too long to contain the answer" : "OpenCode returned no answer";
                await RecordAsync(workspace, "agent", role, role, attempt, started, "failed", result.ExitCode, false, promptText, result.Stdout, problem, spent);
                continue;
            }
            var (value, error2) = parse(text);
            if (value is not null)
            {
                await RecordAsync(workspace, "agent", role, role, attempt, started, "accepted", result.ExitCode, false, promptText, text, null, spent);
                return new AgentOutcome<T>(value, false, null);
            }
            problem = error2;
            await RecordAsync(workspace, "agent", role, role, attempt, started, "answer rejected", result.ExitCode, false, promptText, text, problem, spent);
        }
        return new AgentOutcome<T>(null, false, problem);
    }
}
