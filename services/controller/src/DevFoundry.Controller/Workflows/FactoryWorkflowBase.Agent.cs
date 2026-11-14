using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public abstract partial class FactoryWorkflowBase
{
    /// <param name="Infrastructure">The run failed because of the factory's own machinery (see <see cref="InfrastructureFailure"/>), so asking again may work.</param>
    private readonly List<TokenUsage> usages = [];

    /// <summary>Everything the agents of this run have used so far, or null if none has run (or none reported).</summary>
    protected TokenUsage? TotalUsage => usages.Count == 0 ? null : TokenUsage.Sum(usages);

    protected sealed record AgentOutcome<T>(T? Value, bool NotSignedIn, string? Problem, bool Infrastructure = false) where T : class
    {
        /// <summary>The problem as it goes on the ticket; an infrastructure failure also says that <c>/retry</c> is the way forward.</summary>
        public string Describe() =>
            TicketComments.Inert(Short(Problem ?? "unknown problem")) + (Infrastructure ? TicketComments.RetryHint : "");

        /// <summary>The status a run ends with when this outcome has no value: the stage's own, or the infrastructure one.</summary>
        public string FailedStatus(string stageStatus) => Infrastructure ? Statuses.InfrastructureFailed : stageStatus;
    }

    /// <summary>
    /// Runs one agent role in a read-only sandbox with model access and parses its answer. An unusable
    /// answer gets one more try that says what was wrong; after that the caller asks a person.
    /// </summary>
    protected async Task<AgentOutcome<T>> RunAgentAsync<T>(
        string role, string image, WorkspaceInfo workspace, Func<string?, string> prompt, Func<string, (T? Value, string? Error)> parse,
        bool writable = false, int timeoutSeconds = 900)
        where T : class
    {
        string? problem = null;
        for (var attempt = 1; attempt <= MaxAgentAttempts; attempt++)
        {
            // A budget that is used up ends the run here; a daily limit makes it wait. Never in the middle of an agent's run.
            await EnforceTokenLimitsAsync();
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
                    // A model run can consume plan usage; the retry further down is a deliberate, bounded second try.
                    ActivityPolicy.Sandbox(TimeSpan.FromSeconds(timeoutSeconds + 300), attempts: 1));
            }
            catch (ActivityFailureException error)
            {
                var infrastructure = InfrastructureFailure.Is(error);
                var message = infrastructure ? InfrastructureFailure.Describe(error) : error.InnerException?.Message ?? error.Message;
                await RecordAsync(workspace, RecordStep.Agent(role, attempt, started, "failed", promptText) with { ExitCode = -1, Problem = message });
                return message.Contains("not signed in", StringComparison.OrdinalIgnoreCase)
                    ? new AgentOutcome<T>(null, true, null)
                    : new AgentOutcome<T>(null, false, message, infrastructure);
            }

            var spent = OpenCodeEvents.Usage(result.Stdout, result.OutputTruncated);
            if (spent is not null)
            {
                usages.Add(spent);
            }
            if (result.ExitCode == 78)
            {
                await RecordAsync(workspace, RecordStep.Agent(role, attempt, started, "not signed in", promptText) with { ExitCode = result.ExitCode, Output = result.Stderr, Problem = "OpenCode is not signed in" });
                return new AgentOutcome<T>(null, true, null);
            }
            if (result.TimedOut)
            {
                problem = $"the {role} ran out of time";
                await RecordAsync(workspace, RecordStep.Agent(role, attempt, started, "failed", promptText) with { ExitCode = result.ExitCode, TimedOut = true, Output = result.Stderr, Problem = problem, Usage = spent });
                continue;
            }
            if (result.ExitCode != 0 && ProviderLimit.Detect(result.Stdout, result.Stderr) is { } limitSaid)
            {
                await RecordAsync(workspace, RecordStep.Agent(role, attempt, started, "usage limit", promptText) with { ExitCode = result.ExitCode, Output = result.Stderr, Problem = limitSaid, Usage = spent });
                throw new RunStopException(Statuses.UsageLimit, TicketComments.ProviderLimitReached(role, limitSaid));
            }
            if (result.ExitCode != 0)
            {
                problem = $"OpenCode exited with code {result.ExitCode}: {Short(result.Stderr)}";
                await RecordAsync(workspace, RecordStep.Agent(role, attempt, started, "failed", promptText) with { ExitCode = result.ExitCode, Output = result.Stderr, Problem = problem, Usage = spent });
                continue;
            }

            var text = OpenCodeEvents.FinalText(result.Stdout);
            if (text is null && ProviderLimit.Detect(result.Stdout, result.Stderr) is { } noAnswerLimit)
            {
                await RecordAsync(workspace, RecordStep.Agent(role, attempt, started, "usage limit", promptText) with { ExitCode = result.ExitCode, Output = result.Stdout, Problem = noAnswerLimit, Usage = spent });
                throw new RunStopException(Statuses.UsageLimit, TicketComments.ProviderLimitReached(role, noAnswerLimit));
            }
            if (text is null)
            {
                problem = result.OutputTruncated ? "the output was too long to contain the answer" : "OpenCode returned no answer";
                await RecordAsync(workspace, RecordStep.Agent(role, attempt, started, "failed", promptText) with { ExitCode = result.ExitCode, Output = result.Stdout, Problem = problem, Usage = spent });
                continue;
            }
            var (value, error2) = parse(text);
            if (value is not null)
            {
                await RecordAsync(workspace, RecordStep.Agent(role, attempt, started, "accepted", promptText) with { ExitCode = result.ExitCode, Output = text, Usage = spent });
                return new AgentOutcome<T>(value, false, null);
            }
            problem = error2;
            await RecordAsync(workspace, RecordStep.Agent(role, attempt, started, "answer rejected", promptText) with { ExitCode = result.ExitCode, Output = text, Problem = problem, Usage = spent });
        }
        return new AgentOutcome<T>(null, false, problem);
    }
}
