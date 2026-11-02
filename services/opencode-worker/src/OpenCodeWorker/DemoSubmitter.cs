using System.Text.Json;
using Temporalio.Client;

namespace OpenCodeWorker;

/// <summary>Starts the demo workflow on the controller worker and prints its result.</summary>
public static class DemoSubmitter
{
    private const string DemoPrompt =
        "Create exactly two files in the current workspace: hello.py and test_hello.py. "
        + "hello.py must define greet(name: str) -> str returning 'Hello, ' plus the name plus '!'. "
        + "test_hello.py must contain Python unittest tests for greet('Factory') and greet('World'). "
        + "Use file editing tools. Do not run commands, access external directories, or use network tools. "
        + "Finish with a short description of the two files.";

    public static async Task RunAsync(Settings settings)
    {
        var client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions(settings.TemporalAddress)
            {
                Namespace = settings.TemporalNamespace,
            });
        var taskId = Guid.NewGuid().ToString();
        var handle = await client.StartWorkflowAsync(
            "OpenCodeDemoWorkflow",
            [new OpenCodeTask(taskId, DemoPrompt, settings.OpenCodeTaskQueue)],
            new WorkflowOptions($"opencode-demo-{taskId}", settings.TemporalTaskQueue)
            {
                ExecutionTimeout = TimeSpan.FromMinutes(6),
            });
        Console.WriteLine($"Workflow: {handle.Id}");
        var result = await handle.GetResultAsync<JsonElement>().WaitAsync(TimeSpan.FromSeconds(330));
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
