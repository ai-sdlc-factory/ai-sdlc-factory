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
    /// Keeps one step (what an agent was told and answered, or what a check printed) for the committed record.
    /// A failure to record never changes how the run goes.
    /// </summary>
    protected async Task RecordAsync(
        WorkspaceInfo workspace, string kind, string slug, string title, int attempt, DateTime started, string outcome,
        long exitCode, bool timedOut, string input, string output, string? problem, TokenUsage? usage = null)
    {
        if (recordDirectory is null)
        {
            return;
        }
        var entry = new RecordEntry(
            workspace.Name, ++recordSequence, kind, slug, title, attempt, started.ToString("u"),
            Math.Round((Workflow.UtcNow - started).TotalSeconds, 1), outcome, exitCode, timedOut, input, output, problem, Usage: usage);
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
