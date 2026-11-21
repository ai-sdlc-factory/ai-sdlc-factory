using System.Net.Http.Json;
using System.Text.Json;

namespace DevFoundry.AlertIntake;

/// <summary>The tickets of a GitHub host (github.com or a GitHub Enterprise Server), over its REST API. The labels of a new ticket are made by GitHub when the repository has not got them.</summary>
public sealed class GitHubClient(HttpClient http) : ITicketHost
{
    private const int PageSize = 50;
    private const int MaxPages = 4;

    /// <summary>Where the REST API of a host is: <c>https://api.github.com</c> for github.com, <c>/api/v3</c> under the address of an Enterprise Server.</summary>
    public static string ApiBase(string url) =>
        url.TrimEnd('/').Equals("https://github.com", StringComparison.OrdinalIgnoreCase) ? "https://api.github.com" : url.TrimEnd('/') + "/api/v3";

    /// <summary>Gives a client the headers GitHub asks for.</summary>
    public static void Prepare(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dev-foundry-alert-intake");
        if (token.Length > 0)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static string RepoPath(string owner, string repo) => $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    public Task<int?> FindOpenTicketAsync(string owner, string repo, string key, CancellationToken cancellationToken)
    {
        var marker = TicketText.Marker(key);
        return FindOpenAsync(owner, repo, issue => BodyContains(issue, marker), cancellationToken);
    }

    public Task<int?> FindRecentTicketAsync(string owner, string repo, string groupMarker, DateTimeOffset notBefore, CancellationToken cancellationToken) =>
        FindOpenAsync(owner, repo, issue => BodyContains(issue, groupMarker) && CreatedAtOrAfter(issue, notBefore), cancellationToken);

    /// <summary>The number of the first open ticket that <paramref name="matches"/>, reading at most <see cref="MaxPages"/> pages. GitHub lists pull requests among the issues; they are skipped.</summary>
    private async Task<int?> FindOpenAsync(string owner, string repo, Func<JsonElement, bool> matches, CancellationToken cancellationToken)
    {
        for (var page = 1; page <= MaxPages; page++)
        {
            using var response = await http.GetAsync($"{RepoPath(owner, repo)}/issues?state=open&per_page={PageSize}&page={page}", cancellationToken);
            await EnsureAsync(response, "ticket list");
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }
            foreach (var issue in document.RootElement.EnumerateArray())
            {
                if (!issue.TryGetProperty("pull_request", out _) && matches(issue) && issue.TryGetProperty("number", out var number))
                {
                    return number.GetInt32();
                }
            }
        }
        return null;
    }

    private static bool BodyContains(JsonElement issue, string marker) =>
        issue.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String && body.GetString()!.Contains(marker, StringComparison.Ordinal);

    private static bool CreatedAtOrAfter(JsonElement issue, DateTimeOffset notBefore) =>
        issue.TryGetProperty("created_at", out var created) && created.TryGetDateTimeOffset(out var at) && at >= notBefore;

    public async Task AddMarkerAsync(string owner, string repo, int number, string marker, CancellationToken cancellationToken)
    {
        using var current = await http.GetAsync($"{RepoPath(owner, repo)}/issues/{number}", cancellationToken);
        await EnsureAsync(current, "ticket");
        using var document = await JsonDocument.ParseAsync(await current.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var body = document.RootElement.TryGetProperty("body", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
        if (body.Contains(marker, StringComparison.Ordinal))
        {
            return;
        }
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{RepoPath(owner, repo)}/issues/{number}") { Content = JsonContent.Create(new { body = body.TrimEnd() + "\n" + marker }) };
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureAsync(response, "ticket update");
    }

    public async Task<IssueRef> CreateTicketAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"{RepoPath(owner, repo)}/issues", new { title, body, labels }, cancellationToken);
        await EnsureAsync(response, "ticket");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return new IssueRef(document.RootElement.GetProperty("number").GetInt32(), document.RootElement.GetProperty("html_url").GetString() ?? "");
    }

    public async Task CommentAsync(string owner, string repo, int number, string body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"{RepoPath(owner, repo)}/issues/{number}/comments", new { body }, cancellationToken);
        await EnsureAsync(response, "comment");
    }

    private static Task EnsureAsync(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
        {
            // Only the status: the body may echo what we sent.
            throw new HttpRequestException($"GitHub refused the {what} request: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        return Task.CompletedTask;
    }
}
