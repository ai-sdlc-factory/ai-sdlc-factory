using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public abstract partial class FactoryWorkflowBase
{
    // Set when the project asks for a record (.factory/project.json "record": true): where in the repository the
    // prompts, answers and check output of this run are committed, and the number of the next recorded step.
    protected string? recordDirectory;
    protected int recordSequence;

    /// <summary>Where and how the record of this run is committed, or null when the project did not ask for one.</summary>
    protected RunRecordSpec? RecordSpec(WorkRequest request, string outcome) =>
        recordDirectory is null ? null : new RunRecordSpec(recordDirectory, $"{request.Owner}/{request.Repo}#{request.IssueNumber}: {request.Title}", outcome);

    /// <summary>
    /// What to keep of one step (what an agent was told and answered, or what a check printed) for the committed record. The five
    /// positional values say which step it was and how it ended; the rest is filled in with <c>with { ... }</c> as far as it is known.
    /// </summary>
    protected sealed record RecordStep(string Kind, string Slug, string Title, int Attempt, DateTime Started, string Outcome, string Input)
    {
        public long ExitCode { get; init; }
        public bool TimedOut { get; init; }
        public string Output { get; init; } = "";
        public string? Problem { get; init; }
        public TokenUsage? Usage { get; init; }

        /// <summary>A run of an agent. <paramref name="prompt"/> is what it was told.</summary>
        public static RecordStep Agent(string role, int attempt, DateTime started, string outcome, string prompt) =>
            new("agent", role, role, attempt, started, outcome, prompt);

        /// <summary>One of the project's checks. <paramref name="command"/> is what ran.</summary>
        public static RecordStep Gate(string name, DateTime started, string outcome, string command) =>
            new("gate", $"gate-{name}", $"check: {name}", 1, started, outcome, command);
    }

    /// <summary>Keeps one step for the committed record. A failure to record never changes how the run goes.</summary>
    protected async Task RecordAsync(WorkspaceInfo workspace, RecordStep step)
    {
        if (recordDirectory is null)
        {
            return;
        }
        var entry = new RecordEntry(
            workspace.Name, ++recordSequence, step.Kind, step.Slug, step.Title, step.Attempt, step.Started.ToString("u"),
            Math.Round((Workflow.UtcNow - step.Started).TotalSeconds, 1), step.Outcome, step.ExitCode, step.TimedOut, step.Input, step.Output, step.Problem,
            Usage: step.Usage);
        try
        {
            await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.RecordAsync(entry),
                ActivityPolicy.Local(TimeSpan.FromMinutes(1), 3));
        }
        catch (ActivityFailureException)
        {
            // Not worth stopping a run for.
        }
    }
}
