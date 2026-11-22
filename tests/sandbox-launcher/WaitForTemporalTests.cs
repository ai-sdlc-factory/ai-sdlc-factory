using Microsoft.Extensions.Logging.Abstractions;

namespace AiSdlcFactory.SandboxLauncher.Tests;

public sealed class WaitForTemporalTests
{
    private static WaitForTemporal Waiter(string address, Func<string, int, CancellationToken, Task<bool>> probe, List<TimeSpan> pauses) =>
        new(address, NullLogger<WaitForTemporal>.Instance, probe, (span, _) => { pauses.Add(span); return Task.CompletedTask; });

    [Fact]
    public async Task A_reachable_server_is_not_waited_for()
    {
        var pauses = new List<TimeSpan>();

        await Waiter("temporal:7233", (_, _, _) => Task.FromResult(true), pauses).StartAsync(CancellationToken.None);

        Assert.Empty(pauses);
    }

    [Fact]
    public async Task A_server_that_comes_up_later_is_waited_for_with_growing_pauses()
    {
        var pauses = new List<TimeSpan>();
        var tries = 0;

        await Waiter("temporal:7233", (host, port, _) =>
        {
            Assert.Equal(("temporal", 7233), (host, port));
            return Task.FromResult(++tries > 4);
        }, pauses).StartAsync(CancellationToken.None);

        Assert.Equal([1, 2, 4, 8], pauses.Select(p => (int)p.TotalSeconds));
    }

    [Fact]
    public async Task A_server_that_never_comes_up_is_given_up_on_after_the_limit_so_the_worker_can_fail_loudly()
    {
        var pauses = new List<TimeSpan>();

        await Waiter("temporal:7233", (_, _, _) => Task.FromResult(false), pauses).StartAsync(CancellationToken.None);

        Assert.True(pauses.Sum(p => p.TotalSeconds) <= WaitForTemporal.MaxWait.TotalSeconds);
        Assert.True(pauses.Sum(p => p.TotalSeconds) > WaitForTemporal.MaxWait.TotalSeconds - 11);
        Assert.Equal(10, pauses.Max(p => (int)p.TotalSeconds));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData(":7233")]
    [InlineData("host:notaport")]
    [InlineData("host:70000")]
    public async Task An_address_in_an_unknown_shape_is_left_to_the_worker(string address)
    {
        var tried = false;

        await Waiter(address, (_, _, _) => { tried = true; return Task.FromResult(false); }, []).StartAsync(CancellationToken.None);

        Assert.False(tried);
    }

    [Fact]
    public async Task The_real_probe_reports_a_name_that_does_not_resolve_as_unreachable_and_a_listening_port_as_reachable()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var pauses = new List<TimeSpan>();

        await new WaitForTemporal($"127.0.0.1:{port}", NullLogger<WaitForTemporal>.Instance, null, (span, _) => { pauses.Add(span); return Task.CompletedTask; })
            .StartAsync(CancellationToken.None);

        Assert.Empty(pauses);
    }
}
