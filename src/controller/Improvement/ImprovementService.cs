using Temporalio.Client;
using Temporalio.Exceptions;

namespace DevFoundry.Controller;

/// <summary>
/// Starts the weekly evaluation: a few minutes after the controller starts and then every hour it makes sure there is a run for the last completed week (Monday to Monday, UTC).
/// The run's id is the week's, so asking again, or restarting the controller, never makes a second report for a week; a run that failed may be started again.
/// What the run does (and that it only reads, and never applies anything) is described on <see cref="ImprovementWorkflow"/>.
/// </summary>
public sealed class ImprovementService(ITemporalClient client, WorkerSettings settings, ILogger<ImprovementService> log, Func<DateTimeOffset>? clock = null) : BackgroundService
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.ImprovementEnabled)
        {
            log.LogInformation("The weekly improvement report is switched off (FACTORY_IMPROVEMENT=false)");
            return;
        }
        try
        {
            // A restart storm should not start anything each time, and nothing is urgent.
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                try
                {
                    await StartForLastWeekAsync();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log.LogWarning(error, "Could not start the weekly improvement run; trying again in an hour");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Starts the run for the last completed week. Returns false when there already is one (running, or finished well).</summary>
    public async Task<bool> StartForLastWeekAsync()
    {
        var request = ImprovementRequest.ForLastWeek(now(), settings.ImprovementRepository);
        try
        {
            await client.StartWorkflowAsync(
                (ImprovementWorkflow workflow) => workflow.RunAsync(request),
                new WorkflowOptions(request.WorkflowId, settings.TemporalTaskQueue) { IdReusePolicy = Temporalio.Api.Enums.V1.WorkflowIdReusePolicy.AllowDuplicateFailedOnly });
            log.LogInformation("Started the improvement run {Id} for {From} to {To}", request.WorkflowId, request.From, request.To);
            return true;
        }
        catch (WorkflowAlreadyStartedException)
        {
            return false;
        }
    }
}
