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
    Task<bool> NetworkExistsAsync(string name, CancellationToken cancellationToken);
    Task EnsureNetworkAsync(string name, CancellationToken cancellationToken);
    Task<bool> VolumeExistsAsync(string name, CancellationToken cancellationToken);
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

    public async Task<bool> VolumeExistsAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"volumes/{Uri.EscapeDataString(name)}", null, cancellationToken);
            return true;
        }
        catch (DockerException error) when (error.Status == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<bool> NetworkExistsAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"networks/{Uri.EscapeDataString(name)}", null, cancellationToken);
            return true;
        }
        catch (DockerException error) when (error.Status == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task EnsureNetworkAsync(string name, CancellationToken cancellationToken)
    {
        if (await NetworkExistsAsync(name, cancellationToken))
        {
            return;
        }
        // An ordinary bridge with outbound internet: used only when egress restrictions are switched off.
        var body = new { Name = name, Driver = "bridge", Labels = new Dictionary<string, string> { ["dev-foundry.egress"] = "open" } };
        try
        {
            using var created = await SendAsync(HttpMethod.Post, "networks/create", JsonContent.Create(body, options: Json), cancellationToken);
        }
        catch (DockerException error) when (error.Status == HttpStatusCode.Conflict)
        {
            // Created concurrently.
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
    /// <summary>
    /// Reads the frames and keeps the last <paramref name="maxBytesPerStream"/> bytes of each stream:
    /// for a long-running command the end (the result, the error) matters more than the start.
    /// </summary>
    public static async Task<DockerLogs> ReadAsync(Stream stream, int maxBytesPerStream, CancellationToken cancellationToken)
    {
        var stdout = new TailBuffer(maxBytesPerStream);
        var stderr = new TailBuffer(maxBytesPerStream);
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
                target.Append(buffer, read);
            }
        }
        return new DockerLogs(stdout.Text(), stderr.Text(), stdout.Truncated || stderr.Truncated);
    }

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

    private sealed class TailBuffer(int capacity)
    {
        private readonly byte[] data = new byte[capacity];
        private int start;
        private int count;

        public bool Truncated { get; private set; }

        public void Append(byte[] source, int length)
        {
            if (capacity == 0)
            {
                Truncated |= length > 0;
                return;
            }
            for (var i = 0; i < length; i++)
            {
                if (count == capacity)
                {
                    data[start] = source[i];
                    start = (start + 1) % capacity;
                    Truncated = true;
                }
                else
                {
                    data[(start + count) % capacity] = source[i];
                    count++;
                }
            }
        }

        public string Text()
        {
            var ordered = new byte[count];
            for (var i = 0; i < count; i++)
            {
                ordered[i] = data[(start + i) % capacity];
            }
            return Encoding.UTF8.GetString(ordered);
        }
    }
}
