using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DevFoundry.EgressProxy.Tests;

/// <summary>The real proxy, listening on loopback, in front of a fake upstream that echoes what it receives.</summary>
public sealed class ProxyHarness : IAsyncDisposable
{
    private readonly TcpListener upstream = new(IPAddress.Loopback, 0);
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task running;

    public int UpstreamPort { get; }
    public int ProxyPort { get; }
    public List<AccessEntry> Log { get; } = [];
    public int UpstreamConnections;

    private sealed class Log_(ProxyHarness owner) : IAccessLog
    {
        public void Write(AccessEntry entry)
        {
            lock (owner.Log) { owner.Log.Add(entry); }
        }
    }

    private sealed class FakeResolver(Dictionary<string, IPAddress[]> names) : IResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            names.TryGetValue(host, out var addresses) ? Task.FromResult(addresses) : throw new SocketException((int)SocketError.HostNotFound);
    }

    public ProxyHarness(
        string[]? hosts = null, bool audit = false, ProxyOptions? options = null,
        Dictionary<string, IPAddress[]>? names = null, bool mapLoopback = true)
    {
        upstream.Start();
        UpstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        _ = EchoAsync();

        var config = new EgressConfig(
            new HashSet<int> { UpstreamPort },
            new Dictionary<string, EgressProfile> { ["model"] = new("model", hosts ?? ["allowed.example.com"], audit) });
        var policy = new EgressPolicy(config, mapLoopback
            ? new Dictionary<IPAddress, string> { [IPAddress.Loopback] = "model" }
            : new Dictionary<IPAddress, string> { [IPAddress.Parse("10.99.99.2")] = "model" });
        var resolver = new FakeResolver(names ?? new() { ["allowed.example.com"] = [IPAddress.Loopback], ["other.example.net"] = [IPAddress.Loopback] });
        var server = new ProxyServer(policy, resolver, new Log_(this), options ?? new ProxyOptions { AllowPrivateAddresses = true });

        listener.Start(); // the proxy starts it again; binding first gives us the port
        ProxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        running = server.RunAsync(new TcpListener(IPAddress.Loopback, ProxyPort), stop.Token);
    }

    private async Task EchoAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await upstream.AcceptTcpClientAsync(); } catch { return; }
            Interlocked.Increment(ref UpstreamConnections);
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    try
                    {
                        int read;
                        while ((read = await stream.ReadAsync(buffer)) > 0)
                        {
                            await stream.WriteAsync(buffer.AsMemory(0, read));
                        }
                    }
                    catch (IOException) { }
                }
            });
        }
    }

    public void StopUpstream() => upstream.Stop();

    public async Task<TcpClient> ConnectAsync()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, ProxyPort);
                return client;
            }
            catch (SocketException)
            {
                client.Dispose();
                await Task.Delay(50);
            }
        }
        throw new TimeoutException("the proxy did not start");
    }

    /// <summary>Sends a raw request head and returns the response head and body text.</summary>
    public async Task<(int Status, string Text, TcpClient Client)> SendAsync(string request)
    {
        var client = await ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buffer = new byte[8192];
        var length = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
            if (read == 0) break;
            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            if (text.Contains("\r\n\r\n") && (text.StartsWith("HTTP/1.1 200") || text.Contains("Content-Length")))
            {
                // For a refusal, wait for the body too.
                var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
                var marker = "Content-Length: ";
                var at = text.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0 || length >= headerEnd + int.Parse(text[(at + marker.Length)..].Split('\r')[0])) break;
            }
        }
        var response = Encoding.ASCII.GetString(buffer, 0, length);
        return (int.Parse(response.Split(' ')[1]), response, client);
    }

    public string Connect(string host, int? port = null) =>
        $"CONNECT {host}:{port ?? UpstreamPort} HTTP/1.1\r\nHost: {host}\r\n\r\n";

    public async Task<AccessEntry> NextLogAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            lock (Log)
            {
                if (Log.Count > 0) return Log[^1];
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("nothing was logged");
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        upstream.Stop();
        try { await running; } catch { }
    }
}

public sealed class ProxyServerTests
{
    [Fact]
    public async Task An_allowed_host_gets_a_tunnel_that_carries_bytes_both_ways_and_is_logged()
    {
        await using var proxy = new ProxyHarness();

        var (status, _, client) = await proxy.SendAsync(proxy.Connect("allowed.example.com"));
        using (client)
        {
            Assert.Equal(200, status);
            var stream = client.GetStream();
            await stream.WriteAsync("hello through the tunnel"u8.ToArray());
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer);
            Assert.Equal("hello through the tunnel", Encoding.ASCII.GetString(buffer, 0, read));
            client.Client.Shutdown(SocketShutdown.Send);
            Assert.Equal(0, await stream.ReadAsync(buffer)); // the end of the tunnel reaches the client too
        }

        var entry = await proxy.NextLogAsync();
        Assert.Equal(("model", "allowed.example.com", "allow"), (entry.Profile, entry.Host, entry.Decision));
        Assert.Equal(24, entry.BytesUp);
        Assert.Equal(24, entry.BytesDown);
    }

    [Fact]
    public async Task A_host_that_is_not_listed_is_refused_and_the_upstream_is_never_touched()
    {
        await using var proxy = new ProxyHarness();

        var (status, text, client) = await proxy.SendAsync(proxy.Connect("other.example.net"));
        client.Dispose();

        Assert.Equal(403, status);
        Assert.Contains("not on the allowlist", text);
        Assert.Equal(0, proxy.UpstreamConnections);
        var entry = await proxy.NextLogAsync();
        Assert.Equal(("deny", "other.example.net"), (entry.Decision, entry.Host));
    }

    [Fact]
    public async Task Audit_profiles_let_everything_through_but_log_it_as_audit()
    {
        await using var proxy = new ProxyHarness(audit: true);

        var (status, _, client) = await proxy.SendAsync(proxy.Connect("other.example.net"));
        client.Dispose();

        Assert.Equal(200, status);
        var entry = await proxy.NextLogAsync();
        Assert.Equal("audit", entry.Decision);
    }

    [Theory]
    [InlineData("GET http://allowed.example.com/ HTTP/1.1\r\nHost: allowed.example.com\r\n\r\n", 405)]
    [InlineData("POST http://allowed.example.com/ HTTP/1.1\r\n\r\n", 405)]
    [InlineData("CONNECT allowed.example.com HTTP/1.1\r\n\r\n", 400)]
    [InlineData("garbage\r\n\r\n", 400)]
    public async Task Anything_but_a_well_formed_connect_is_refused(string request, int expected)
    {
        await using var proxy = new ProxyHarness();

        var (status, _, client) = await proxy.SendAsync(request);
        client.Dispose();

        Assert.Equal(expected, status);
        Assert.Equal(0, proxy.UpstreamConnections);
    }

    [Fact]
    public async Task A_listed_name_that_points_at_a_private_address_is_refused()
    {
        await using var proxy = new ProxyHarness(
            names: new() { ["allowed.example.com"] = [IPAddress.Parse("169.254.169.254")] },
            options: new ProxyOptions());

        var (status, text, client) = await proxy.SendAsync(proxy.Connect("allowed.example.com"));
        client.Dispose();

        Assert.Equal(403, status);
        Assert.Contains("private or reserved", text);
        Assert.Equal(0, proxy.UpstreamConnections);
        Assert.Contains("private or reserved", (await proxy.NextLogAsync()).Reason);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_a_bad_gateway()
    {
        await using var proxy = new ProxyHarness(names: new());

        var (status, _, client) = await proxy.SendAsync(proxy.Connect("allowed.example.com"));
        client.Dispose();

        Assert.Equal(502, status);
    }

    [Fact]
    public async Task An_unreachable_upstream_is_a_bad_gateway_and_is_logged()
    {
        await using var proxy = new ProxyHarness();
        proxy.StopUpstream();

        var (status, _, client) = await proxy.SendAsync(proxy.Connect("allowed.example.com"));
        client.Dispose();

        Assert.Equal(502, status);
        var entry = await proxy.NextLogAsync();
        Assert.Equal(("deny", "could not connect"), (entry.Decision, entry.Reason));
    }

    [Fact]
    public async Task A_port_outside_the_policy_is_refused_before_any_connection_is_tried()
    {
        await using var proxy = new ProxyHarness();

        var (status, _, client) = await proxy.SendAsync(proxy.Connect("allowed.example.com", port: 25));
        client.Dispose();

        Assert.Equal(403, status);
        Assert.Contains("port 25", (await proxy.NextLogAsync()).Reason);
        Assert.Equal(0, proxy.UpstreamConnections);
    }

    [Fact]
    public async Task An_address_that_belongs_to_no_profile_gets_nothing()
    {
        await using var proxy = new ProxyHarness(mapLoopback: false);

        var (status, text, client) = await proxy.SendAsync(proxy.Connect("allowed.example.com"));
        client.Dispose();

        Assert.Equal(403, status);
        Assert.Contains("no egress profile", text);
        Assert.Equal(0, proxy.UpstreamConnections);
    }

    [Fact]
    public async Task Bytes_sent_right_after_the_request_are_forwarded()
    {
        await using var proxy = new ProxyHarness();
        var client = await proxy.ConnectAsync();
        using (client)
        {
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(proxy.Connect("allowed.example.com") + "early-bytes"));
            var buffer = new byte[256];
            var text = "";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!text.EndsWith("early-bytes"))
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                Assert.NotEqual(0, read);
                text += Encoding.ASCII.GetString(buffer, 0, read);
            }
            Assert.StartsWith("HTTP/1.1 200", text);
        }
    }

    [Fact]
    public async Task An_oversized_request_head_is_refused()
    {
        await using var proxy = new ProxyHarness(options: new ProxyOptions { AllowPrivateAddresses = true, MaxHeadBytes = 512 });

        var (status, _, client) = await proxy.SendAsync($"CONNECT allowed.example.com:{proxy.UpstreamPort} HTTP/1.1\r\nX-Padding: {new string('a', 2000)}\r\n\r\n");
        client.Dispose();

        Assert.Equal(431, status);
        Assert.Equal(0, proxy.UpstreamConnections);
    }

    [Fact]
    public async Task A_silent_tunnel_is_closed_after_the_idle_timeout()
    {
        await using var proxy = new ProxyHarness(options: new ProxyOptions { AllowPrivateAddresses = true, IdleTimeout = TimeSpan.FromMilliseconds(300) });

        var (status, _, client) = await proxy.SendAsync(proxy.Connect("allowed.example.com"));
        using (client)
        {
            Assert.Equal(200, status);
            var buffer = new byte[16];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal(0, await client.GetStream().ReadAsync(buffer, timeout.Token));
        }
    }

    [Fact]
    public async Task A_client_that_connects_and_closes_without_a_request_is_not_logged()
    {
        await using var proxy = new ProxyHarness();

        using (var client = await proxy.ConnectAsync()) { }
        await Task.Delay(300);

        Assert.Empty(proxy.Log);
    }

    [Fact]
    public async Task Connections_beyond_the_limit_are_turned_away_while_others_keep_working()
    {
        await using var proxy = new ProxyHarness(options: new ProxyOptions { AllowPrivateAddresses = true, MaxConnections = 1 });

        var (status, _, first) = await proxy.SendAsync(proxy.Connect("allowed.example.com"));
        using (first)
        {
            Assert.Equal(200, status);
            var (second, _, other) = await proxy.SendAsync(proxy.Connect("allowed.example.com"));
            other.Dispose();
            Assert.Equal(503, second);
        }
    }
}
