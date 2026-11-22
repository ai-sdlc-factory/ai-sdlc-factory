using System.Net.Sockets;

namespace AiSdlcFactory.SandboxLauncher;

/// <summary>
/// Holds the launcher's start until the Temporal server can be reached. Without it, a launcher started while Temporal is still (re)starting, so its name does not resolve yet,
/// has its worker fail on the first connection and the whole process stop; only Docker's restart policy then brought it back. Registered before the worker, so it runs first.
/// It gives up waiting after <see cref="MaxWait"/> and lets the worker try (and fail loudly) as before, so a wrong address is not hidden.
/// </summary>
public sealed class WaitForTemporal(string address, ILogger<WaitForTemporal> logger, Func<string, int, CancellationToken, Task<bool>>? probe = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    : IHostedService
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);
    private readonly Func<string, int, CancellationToken, Task<bool>> canConnect = probe ?? TryConnectAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> pause = delay ?? Task.Delay;

    /// <summary>The seconds to wait after the n-th failed try: 1, 2, 4, 8, then 10 at most.</summary>
    public static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(failures, 4)));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!TryParse(address, out var host, out var port))
        {
            return; // an address in a shape this does not understand is left to the worker
        }
        var waited = TimeSpan.Zero;
        for (var failures = 0; ; failures++)
        {
            if (await canConnect(host, port, cancellationToken))
            {
                if (failures > 0)
                {
                    logger.LogInformation("Temporal at {Address} is reachable after {Tries} failed tr{Plural}", address, failures, failures == 1 ? "y" : "ies");
                }
                return;
            }
            var wait = Backoff(failures);
            if (waited + wait > MaxWait)
            {
                logger.LogWarning("Temporal at {Address} is still not reachable after {Seconds} seconds; starting the worker anyway", address, (int)waited.TotalSeconds);
                return;
            }
            if (failures == 0)
            {
                logger.LogWarning("Temporal at {Address} is not reachable yet; waiting for it", address);
            }
            await pause(wait, cancellationToken);
            waited += wait;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static bool TryParse(string address, out string host, out int port)
    {
        host = "";
        port = 0;
        var colon = address.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(address.AsSpan(colon + 1), out port) || port is < 1 or > 65535)
        {
            return false;
        }
        host = address[..colon];
        return host.Length > 0;
    }

    private static async Task<bool> TryConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception error) when ((error is SocketException or OperationCanceledException or IOException) && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
