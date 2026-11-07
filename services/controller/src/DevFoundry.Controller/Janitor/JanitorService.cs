namespace DevFoundry.Controller;

/// <summary>Runs the janitor a few minutes after start and then every few hours, until the worker stops. A failed pass is logged and tried again next time.</summary>
public sealed class JanitorService(JanitorRunner runner, WorkerSettings settings, ILogger<JanitorService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.JanitorEnabled)
        {
            log.LogInformation("The janitor is switched off (FACTORY_JANITOR=false)");
            return;
        }
        try
        {
            // A restart storm should not trigger a pass each time, and nothing is urgent.
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(settings.JanitorIntervalHours));
            do
            {
                try
                {
                    await runner.RunOnceAsync(stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log.LogWarning(error, "Janitor pass failed; trying again next time");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
