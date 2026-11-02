using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevFoundry.SandboxLauncher;

public sealed class DockerException(HttpStatusCode status, string message)
    : Exception($"Docker returned {(int)status}: {message}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>The few Docker operations the launcher uses; faked in unit tests.</summary>
public interface IDockerClient
{
    Task PingAsync(CancellationToken cancellationToken);
    Task EnsureNetworkAsync(string name, CancellationToken cancellationToken);
    Task<string> CreateAsync(CreateContainerBody body, CancellationToken cancellationToken);
    Task StartAsync(string id, CancellationToken cancellationToken);
    Task<long> WaitAsync(string id, CancellationToken cancellationToken);
    Task KillAsync(string id, CancellationToken cancellationToken);
    Task<DockerLogs> LogsAsync(string id, int maxBytes, CancellationToken cancellationToken);
    Task RemoveAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListSandboxIdsAsync(CancellationToken cancellationToken);
}

public sealed record DockerLogs(string Stdout, string Stderr, bool Truncated);

/// <summary>Minimal Docker Engine API client over the unix socket; no third-party dependency.</summary>
public sealed class DockerClient : IDockerClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient http;

    public DockerClient(HttpClient http) => this.http = http;

    public static DockerClient FromSocket(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new DockerClient(new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://docker/"),
            Timeout = Timeout.InfiniteTimeSpan,
        });
    }

    public async Task PingAsync(CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Get, "_ping", null, cancellationToken);

    public async Task EnsureNetworkAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var existing = await SendAsync(HttpMethod.Get, $"networks/{Uri.EscapeDataString(name)}", null, cancellationToken);
            return;
        }
        catch (DockerException error) when (error.Status == HttpStatusCode.NotFound)
        {
            // Created below.
        }

        // A plain bridge: outbound internet works (the agents need their model provider), and Docker
        // keeps it separate from every other network, so no factory service is reachable from it.
        var body = new { Name = name, Driver = "bridge", Labels = new Dictionary<string, string> { [ContainerSpec.Label] = "true" } };
        try
        {
            using var created = await SendAsync(HttpMethod.Post, "networks/create", JsonContent.Create(body, options: Json), cancellationToken);
        }
        catch (DockerException error) when (error.Status == HttpStatusCode.Conflict)
        {
            // Created concurrently by another process.
        }
    }

    public async Task<string> CreateAsync(CreateContainerBody body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "containers/create", JsonContent.Create(body, options: Json), cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<CreateResponse>(cancellationToken);
        return created?.Id ?? throw new DockerException(HttpStatusCode.OK, "create returned no container id");
    }

    public async Task StartAsync(string id, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, $"containers/{id}/start", null, cancellationToken);

    public async Task<long> WaitAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"containers/{id}/wait?condition=not-running", null, cancellationToken);
        var waited = await response.Content.ReadFromJsonAsync<WaitResponse>(cancellationToken);
        return waited?.StatusCode ?? throw new DockerException(HttpStatusCode.OK, "wait returned no status");
    }

    public async Task KillAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(HttpMethod.Post, $"containers/{id}/kill", null, cancellationToken);
        }
        catch (DockerException error) when (error.Status is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            // Already stopped or gone.
        }
    }

    public async Task<DockerLogs> LogsAsync(string id, int maxBytes, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get, $"containers/{id}/logs?stdout=1&stderr=1", null, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await LogDemuxer.ReadAsync(stream, maxBytes, cancellationToken);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, $"containers/{id}?force=true&v=true", null, cancellationToken);
        }
        catch (DockerException error) when (error.Status == HttpStatusCode.NotFound)
        {
            // Already removed.
        }
    }

    public async Task<IReadOnlyList<string>> ListSandboxIdsAsync(CancellationToken cancellationToken)
    {
        var filters = Uri.EscapeDataString($$"""{"label":["{{ContainerSpec.Label}}=true"]}""");
        using var response = await SendAsync(HttpMethod.Get, $"containers/json?all=true&filters={filters}", null, cancellationToken);
        var items = await response.Content.ReadFromJsonAsync<List<ListItem>>(cancellationToken) ?? [];
        return items.Select(item => item.Id).ToList();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new DockerException(response.StatusCode, ExtractMessage(text));
        }
        return response;
    }

    private static string ExtractMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString() ?? body
                : body;
        }
        catch (JsonException)
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }

    private sealed record CreateResponse(string Id);
    private sealed record WaitResponse(long StatusCode);
    private sealed record ListItem(string Id);
}

/// <summary>
/// Docker multiplexes stdout and stderr into one stream of frames: an 8-byte header (stream id,
/// three zero bytes, big-endian length) followed by the payload.
/// </summary>
public static class LogDemuxer
{
    public static async Task<DockerLogs> ReadAsync(Stream stream, int maxBytesPerStream, CancellationToken cancellationToken)
    {
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var truncated = false;
        var header = new byte[8];
        var buffer = new byte[8192];

        while (await ReadExactAsync(stream, header, cancellationToken))
        {
            var length = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
            var target = header[0] == 2 ? stderr : stdout;
            while (length > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length)), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                length -= read;
                var room = maxBytesPerStream - (int)target.Length;
                if (read > room)
                {
                    truncated = true;
                }
                if (room > 0)
                {
                    target.Write(buffer, 0, Math.Min(read, room));
                }
            }
        }
        return new DockerLogs(Decode(stdout), Decode(stderr), truncated);
    }

    private static string Decode(MemoryStream stream) =>
        Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }
}
