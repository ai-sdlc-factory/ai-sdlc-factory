using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevFoundry.SandboxLauncher;

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

    public async Task<DockerImage?> InspectImageAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"images/{name}/json", null, cancellationToken);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;
            var labels = new Dictionary<string, string>();
            if (root.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object
                && config.TryGetProperty("Labels", out var found) && found.ValueKind == JsonValueKind.Object)
            {
                foreach (var label in found.EnumerateObject())
                {
                    labels[label.Name] = label.Value.GetString() ?? "";
                }
            }
            return new DockerImage(root.GetProperty("Id").GetString() ?? "", root.TryGetProperty("Size", out var size) ? size.GetInt64() : 0, labels);
        }
        catch (DockerException error) when (error.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task BuildImageAsync(
        Stream context, string tag, DockerBuildLimits limits, IReadOnlyDictionary<string, string> labels, Action<string> onLine, CancellationToken cancellationToken)
    {
        // The classic builder: it only runs instructions from the context, never fetches a frontend image, and takes the memory and CPU limits.
        var query = $"build?t={Uri.EscapeDataString(tag)}&rm=1&forcerm=1&pull=0&nocache=0"
            + $"&memory={limits.MemoryBytes}&memswap={limits.MemoryBytes}&cpuperiod=100000&cpuquota={(long)(limits.Cpus * 100000)}"
            + $"&labels={Uri.EscapeDataString(JsonSerializer.Serialize(labels))}";
        var content = new StreamContent(context);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        using var response = await SendAsync(HttpMethod.Post, query, content, cancellationToken);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            using var message = JsonDocument.Parse(line);
            if (message.RootElement.TryGetProperty("error", out var error))
            {
                throw new DockerException(HttpStatusCode.InternalServerError, error.GetString() ?? "the build failed");
            }
            if (message.RootElement.TryGetProperty("stream", out var stream) && stream.GetString() is { } text)
            {
                onLine(text);
            }
        }
    }

    public async Task RemoveImageAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, $"images/{name}?force=true", null, cancellationToken);
        }
        catch (DockerException error) when (error.Status == HttpStatusCode.NotFound)
        {
            // Already removed.
        }
    }

    public async Task<IReadOnlyList<DockerImageListing>> ListRepoImagesAsync(CancellationToken cancellationToken)
    {
        var filters = Uri.EscapeDataString("{\"label\":[\"" + RepoImageNames.Label + "=true\"]}");
        using var response = await SendAsync(HttpMethod.Get, $"images/json?filters={filters}", null, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var result = new List<DockerImageListing>();
        foreach (var image in document.RootElement.EnumerateArray())
        {
            if (!image.TryGetProperty("Created", out var created) || !image.TryGetProperty("RepoTags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var tag in tags.EnumerateArray().Select(t => t.GetString()).OfType<string>().Where(t => t.StartsWith(RepoImageNames.Repository + ":", StringComparison.Ordinal)))
            {
                result.Add(new DockerImageListing(tag, DateTimeOffset.FromUnixTimeSeconds(created.GetInt64())));
            }
        }
        return result;
    }

    public async Task<bool> RemoveUnusedImageAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, $"images/{name}?force=false", null, cancellationToken);
            return true;
        }
        catch (DockerException error) when (error.Status == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (DockerException error) when (error.Status == HttpStatusCode.Conflict)
        {
            return false;
        }
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
