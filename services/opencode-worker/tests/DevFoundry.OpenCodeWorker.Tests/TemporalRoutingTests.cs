using System.Runtime.Versioning;
using System.Text.Json;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Worker;

namespace DevFoundry.OpenCodeWorker.Tests;

/// <summary>Temporal routing test; the OpenCode subprocess is explicitly a stub.</summary>
[SupportedOSPlatform("linux")]
public sealed class TemporalRoutingTests
{
    private sealed class StubActivities(FakeOpenCode fake)
    {
        [Activity("run_opencode_task")]
        public Task<OpenCodeResult> RunAsync(OpenCodeTask task) =>
            OpenCodeRunner.ExecuteTaskAsync(task, fake.Settings, fake.Executable);
    }

    [SkippableFact]
    public async Task Demo_routes_to_execution_worker()
    {
        var address = Environment.GetEnvironmentVariable("TEMPORAL_TEST_ADDRESS");
        Skip.If(string.IsNullOrEmpty(address), "Set TEMPORAL_TEST_ADDRESS to test the deployed controller workflow");

        const string script = """
            echo "def greet(name): return name" > hello.py
            echo "# demo test" > test_hello.py
            echo '{}'
            """;
        using var fake = new FakeOpenCode(script);
        var queue = $"opencode-test-{Guid.NewGuid()}";
        var taskId = Guid.NewGuid().ToString();
        var client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions(address!)
            {
                Namespace = Environment.GetEnvironmentVariable("TEMPORAL_TEST_NAMESPACE") ?? "default",
            });

        using var worker = new TemporalWorker(
            client, new TemporalWorkerOptions(queue).AddAllActivities(new StubActivities(fake)));
        var result = await worker.ExecuteAsync(async () =>
        {
            var handle = await client.StartWorkflowAsync(
                "OpenCodeDemoWorkflow",
                [new OpenCodeTask(taskId, "Create demo files.", queue)],
                new WorkflowOptions(
                    $"opencode-test-{taskId}",
                    Environment.GetEnvironmentVariable("TEMPORAL_TASK_QUEUE") ?? "factory-events"));
            return await handle.GetResultAsync<JsonElement>().WaitAsync(TimeSpan.FromSeconds(30));
        });

        Assert.Equal(taskId, result.GetProperty("task_id").GetString());
        Assert.Equal(
            ["hello.py", "test_hello.py"],
            result.GetProperty("artifacts").EnumerateObject().Select(p => p.Name).Order());
    }
}
