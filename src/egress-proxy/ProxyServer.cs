using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DevFoundry.EgressProxy;

public interface IResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemResolver : IResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed record ProxyOptions
{
    public int MaxConnections { get; init; } = 256;
    public int MaxHeadBytes { get; init; } = 8192;
    public TimeSpan HeadTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromHours(2);

    /// <summary>For tests only: lets the proxy tunnel to loopback. Never set from configuration.</summary>
    public bool AllowPrivateAddresses { get; init; }
}

/// <summary>
/// A forward proxy that supports HTTPS tunnels (CONNECT) and nothing else. It never looks inside the tunnel;
/// it decides on the host name, checks where that name really points, and then copies bytes.
/// </summary>
public sealed class ProxyServer(EgressPolicy policy, IResolver resolver, IAccessLog log, ProxyOptions options)
{
    private readonly SemaphoreSlim slots = new(options.MaxConnections);

    public async Task RunAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        listener.Start();
        using var stopping = cancellationToken.Register(listener.Stop);
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }
            _ = HandleSafelyAsync(client, cancellationToken);
        }
    }

    private async Task HandleSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            if (!await slots.WaitAsync(0, cancellationToken))
            {
                await TryRespondAsync(client.GetStream(), 503, "Service Unavailable", "too many open connections");
                return;
            }
            try
            {
                await HandleAsync(client, cancellationToken);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The peer went away; nothing to do.
            }
            finally
            {
                slots.Release();
            }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var stream = client.GetStream();
        var local = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
        var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        var profile = policy.ProfileFor(local);

        var (head, leftover) = await ReadHeadAsync(stream, cancellationToken);
        if (head is null)
        {
            return; // closed without a request (a health check, a port scan); not worth a log line
        }

        void Record(string host, int port, string decision, string reason, long up = 0, long down = 0) =>
            log.Write(new AccessEntry(DateTimeOffset.UtcNow, profile ?? "?", remote, host, port, decision, reason, up, down, clock.ElapsedMilliseconds));

        if (profile is null)
        {
            Record("-", 0, "deny", $"connection to {local}, which belongs to no profile");
            await TryRespondAsync(stream, 403, "Forbidden", "this address belongs to no egress profile");
            return;
        }

        switch (ConnectRequestParser.TryParse(head, out var request))
        {
            case ParseFailure.NotConnect:
                Record(ConnectRequestParser.PlainHttpHost(head), 0, "deny", "only CONNECT is supported (plain HTTP is not proxied)");
                await TryRespondAsync(stream, 405, "Method Not Allowed", "only HTTPS tunnels (CONNECT) are supported");
                return;
            case ParseFailure.Malformed:
                Record("-", 0, "deny", "malformed request");
                await TryRespondAsync(stream, 400, "Bad Request", "malformed request");
                return;
        }

        var decision = policy.Decide(profile, request!.Host, request.Port);
        if (!decision.Allowed)
        {
            Record(request.Host, request.Port, "deny", decision.Reason);
            await TryRespondAsync(stream, 403, "Forbidden", $"egress denied: {decision.Reason}");
            return;
        }

        // Where does the name really point? A listed name must not lead into the private network or the host.
        IPAddress[] addresses;
        try
        {
            addresses = await resolver.ResolveAsync(request.Host, cancellationToken);
        }
        catch (Exception error) when (error is SocketException or ArgumentException)
        {
            Record(request.Host, request.Port, "deny", "the name does not resolve");
            await TryRespondAsync(stream, 502, "Bad Gateway", "the name does not resolve");
            return;
        }
        var usable = addresses.Where(address => options.AllowPrivateAddresses || AddressRules.IsPublic(address)).ToArray();
        if (usable.Length == 0)
        {
            Record(request.Host, request.Port, "deny", addresses.Length == 0 ? "the name does not resolve" : "the name points at a private or reserved address");
            await TryRespondAsync(stream, 403, "Forbidden", "egress denied: the name points at a private or reserved address");
            return;
        }

        using var upstream = await ConnectAsync(usable, request.Port, cancellationToken);
        if (upstream is null)
        {
            Record(request.Host, request.Port, "deny", "could not connect");
            await TryRespondAsync(stream, 502, "Bad Gateway", "could not connect");
            return;
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), cancellationToken);
        var upstreamStream = upstream.GetStream();
        if (leftover.Length > 0)
        {
            await upstreamStream.WriteAsync(leftover, cancellationToken);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(options.MaxDuration);
        var up = CopyAsync(stream, upstreamStream, upstream.Client, limit.Token);
        var down = CopyAsync(upstreamStream, stream, client.Client, limit.Token);
        await Task.WhenAll(up, down);
        Record(request.Host, request.Port, decision.Audit ? "audit" : "allow", decision.Reason, up.Result + leftover.Length, down.Result);
    }

    private async Task<(byte[]? Head, byte[] Leftover)> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HeadTimeout);
        var buffer = new byte[options.MaxHeadBytes];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0)
                {
                    return (null, []);
                }
                length += read;
                var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    return (buffer[..end], buffer[(end + 4)..length]);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Too slow: treated like an oversized head.
        }
        await TryRespondAsync(stream, 431, "Request Header Fields Too Large", "request head too large or too slow");
        return (null, []);
    }

    private async Task<TcpClient?> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        foreach (var address in addresses.Take(4))
        {
            var client = new TcpClient(address.AddressFamily);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.ConnectTimeout);
                await client.ConnectAsync(address, port, timeout.Token);
                return client;
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException)
            {
                client.Dispose();
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }
        return null;
    }

    /// <summary>Copies until one side closes or goes quiet, then closes the sending half so the peer sees the end.</summary>
    private async Task<long> CopyAsync(Stream from, Stream to, Socket destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(options.IdleTimeout);
                int read;
                try
                {
                    read = await from.ReadAsync(buffer, idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break; // idle for too long
                }
                if (read == 0)
                {
                    break;
                }
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
            }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A broken or cut-off tunnel simply ends.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try { destination.Shutdown(SocketShutdown.Send); } catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        }
        return total;
    }

    private static async Task TryRespondAsync(Stream stream, int status, string reason, string body)
    {
        try
        {
            var payload = Encoding.UTF8.GetBytes(body + "\n");
            var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(payload);
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
        {
            // The client is gone.
        }
    }
}
