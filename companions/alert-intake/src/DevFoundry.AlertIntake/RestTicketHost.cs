using System.Net.Http.Json;
using System.Text.Json;

namespace DevFoundry.AlertIntake;

/// <summary>
/// What the ticket clients of the git hosts share: finding an open ticket by a marker in its text, adding a marker, commenting. A host differs only in where its tickets are
/// (<see cref="RepoPath"/>, <see cref="ListQuery"/>), which of its listed issues are tickets (<see cref="IsTicket"/>), how a ticket is opened, and what it calls itself in an error.
/// </summary>
public abstract class RestTicketHost(HttpClient http, string hostName) : ITicketHost
{
    protected const int PageSize = 50;
    protected const int MaxPages = 4;

    protected HttpClient Http => http;

    /// <summary>The path of a repository on the host's API; the owner and the name are escaped, so they cannot change the path.</summary>
    protected abstract string RepoPath(string owner, string repo);

    /// <summary>The query of the list of open tickets, for a page (1 is the first).</summary>
    protected abstract string ListQuery(int page);

    /// <summary>Whether a listed issue is a ticket. GitHub lists pull requests among its issues; they are not.</summary>
    protected virtual bool IsTicket(JsonElement issue) => true;

    public abstract Task<IssueRef> CreateTicketAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken);

    public Task<int?> FindOpenTicketAsync(string owner, string repo, string key, CancellationToken cancellationToken)
    {
        var marker = TicketText.Marker(key);
        return FindOpenAsync(owner, repo, issue => BodyContains(issue, marker), cancellationToken);
    }

    public Task<int?> FindRecentTicketAsync(string owner, string repo, string groupMarker, DateTimeOffset notBefore, CancellationToken cancellationToken) =>
        FindOpenAsync(owner, repo, issue => BodyContains(issue, groupMarker) && CreatedAtOrAfter(issue, notBefore), cancellationToken);

    /// <summary>The number of the first open ticket that <paramref name="matches"/>, reading at most <see cref="MaxPages"/> pages.</summary>
    private async Task<int?> FindOpenAsync(string owner, string repo, Func<JsonElement, bool> matches, CancellationToken cancellationToken)
    {
        for (var page = 1; page <= MaxPages; page++)
        {
            using var response = await http.GetAsync($"{RepoPath(owner, repo)}/issues?{ListQuery(page)}", cancellationToken);
            await EnsureAsync(response, "ticket list");
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }
            foreach (var issue in document.RootElement.EnumerateArray())
            {
                if (IsTicket(issue) && matches(issue) && issue.TryGetProperty("number", out var number))
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

    public async Task CommentAsync(string owner, string repo, int number, string body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"{RepoPath(owner, repo)}/issues/{number}/comments", new { body }, cancellationToken);
        await EnsureAsync(response, "comment");
    }

    /// <summary>Fails with only the status: a body may echo what was sent.</summary>
    protected Task EnsureAsync(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{hostName} refused the {what} request: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        return Task.CompletedTask;
    }

    /// <summary>The number and address of the ticket a host answered a creation with.</summary>
    protected static async Task<IssueRef> ReadIssueAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return new IssueRef(document.RootElement.GetProperty("number").GetInt32(), document.RootElement.GetProperty("html_url").GetString() ?? "");
    }
}
