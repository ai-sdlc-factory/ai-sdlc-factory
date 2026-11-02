using Temporalio.Activities;

namespace DevFoundry.OpenCodeWorker;

public sealed class OpenCodeActivities(Settings settings)
{
    [Activity("run_opencode_task")]
    public async Task<OpenCodeResult> RunOpenCodeTaskAsync(OpenCodeTask task)
    {
        var context = ActivityExecutionContext.Current;
        using var stopPulse = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var pulse = Task.Run(async () =>
        {
            while (!stopPulse.IsCancellationRequested)
            {
                context.Heartbeat("OpenCode execution is active");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stopPulse.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
        try
        {
            return await OpenCodeRunner.ExecuteTaskAsync(
                task, settings, cancellationToken: context.CancellationToken);
        }
        finally
        {
            await stopPulse.CancelAsync();
            await pulse;
        }
    }
}
