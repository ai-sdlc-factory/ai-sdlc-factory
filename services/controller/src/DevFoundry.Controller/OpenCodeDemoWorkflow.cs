using System.Text.Json;
using Temporalio.Common;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

[Workflow("OpenCodeDemoWorkflow")]
public sealed class OpenCodeDemoWorkflow
{
    [WorkflowRun]
    public Task<JsonElement> RunAsync(OpenCodeTask task) =>
        Workflow.ExecuteActivityAsync<JsonElement>(
            "run_opencode_task",
            [task],
            new ActivityOptions
            {
                TaskQueue = task.ActivityTaskQueue ?? "opencode-tasks",
                StartToCloseTimeout = TimeSpan.FromMinutes(5),
                HeartbeatTimeout = TimeSpan.FromSeconds(30),
                // A model call can consume plan usage; do not repeat it automatically.
                RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
            });
}
