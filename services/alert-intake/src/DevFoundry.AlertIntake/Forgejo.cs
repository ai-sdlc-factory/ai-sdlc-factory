using System.Net.Http.Json;
using System.Text.Json;

namespace DevFoundry.AlertIntake;

public sealed record IssueRef(int Number, string Url);

/// <summary>The little of Forgejo the intake needs. A fake stands in for it in the tests.</summary>
public interface IForgejo
{
    /// <summary>The number of an open ticket that carries this alert's marker, or null.</summary>
    Task<int?> FindOpenTicketAsync(string owner, string repo, string key, CancellationToken cancellationToken);

    /// <summary>The number of an open ticket that carries this route marker and was opened at or after <paramref name="notBefore"/>, or null.</summary>
    Task<int?> FindRecentTicketAsync(string owner, string repo, string groupMarker, DateTimeOffset notBefore, CancellationToken cancellationToken);

    /// <summary>Adds a hidden marker to a ticket's text, so a later notice for the alert that was added to it finds the ticket by its own marker.</summary>
    Task AddMarkerAsync(string owner, string repo, int number, string marker, CancellationToken cancellationToken);

    Task<IssueRef> CreateTicketAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken);

    Task CommentAsync(string owner, string repo, int number, string body, CancellationToken cancellationToken);
}

public sealed class ForgejoClient(HttpClient http) : IForgejo
{
    private const int PageSize = 50;
    private const int MaxPages = 4;
    private const string NewLabelColour = "#d73a4a";

    private static string RepoPath(string owner, string repo) => $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    public async Task<int?> FindOpenTicketAsync(string owner, string repo, string key, CancellationToken cancellationToken)
    {
        var marker = TicketText.Marker(key);
        for (var page = 1; page <= MaxPages; page++)
        {
            using var response = await http.GetAsync($"{RepoPath(owner, repo)}/issues?state=open&type=issues&limit={PageSize}&page={page}", cancellationToken);
            await EnsureAsync(response, "ticket list", cancellationToken);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }
            foreach (var issue in document.RootElement.EnumerateArray())
            {
                if (issue.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                    && body.GetString()!.Contains(marker, StringComparison.Ordinal) && issue.TryGetProperty("number", out var number))
                {
                    return number.GetInt32();
                }
            }
        }
        return null;
    }

    public async Task<int?> FindRecentTicketAsync(string owner, string repo, string groupMarker, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        for (var page = 1; page <= MaxPages; page++)
        {
            using var response = await http.GetAsync($"{RepoPath(owner, repo)}/issues?state=open&type=issues&limit={PageSize}&page={page}", cancellationToken);
            await EnsureAsync(response, "ticket list", cancellationToken);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }
            foreach (var issue in document.RootElement.EnumerateArray())
            {
                if (issue.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                    && body.GetString()!.Contains(groupMarker, StringComparison.Ordinal)
                    && issue.TryGetProperty("created_at", out var created) && created.TryGetDateTimeOffset(out var at) && at >= notBefore
                    && issue.TryGetProperty("number", out var number))
                {
                    return number.GetInt32();
                }
            }
        }
        return null;
    }

    public async Task AddMarkerAsync(string owner, string repo, int number, string marker, CancellationToken cancellationToken)
    {
        using var current = await http.GetAsync($"{RepoPath(owner, repo)}/issues/{number}", cancellationToken);
        await EnsureAsync(current, "ticket", cancellationToken);
        using var document = await JsonDocument.ParseAsync(await current.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var body = document.RootElement.TryGetProperty("body", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
        if (body.Contains(marker, StringComparison.Ordinal))
        {
            return;
        }
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{RepoPath(owner, repo)}/issues/{number}") { Content = JsonContent.Create(new { body = body.TrimEnd() + "\n" + marker }) };
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureAsync(response, "ticket update", cancellationToken);
    }

    public async Task<IssueRef> CreateTicketAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        var ids = await LabelIdsAsync(owner, repo, labels, cancellationToken);
        using var response = await http.PostAsJsonAsync($"{RepoPath(owner, repo)}/issues", new { title, body, labels = ids }, cancellationToken);
        await EnsureAsync(response, "ticket", cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return new IssueRef(document.RootElement.GetProperty("number").GetInt32(), document.RootElement.GetProperty("html_url").GetString() ?? "");
    }

    public async Task CommentAsync(string owner, string repo, int number, string body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"{RepoPath(owner, repo)}/issues/{number}/comments", new { body }, cancellationToken);
        await EnsureAsync(response, "comment", cancellationToken);
    }

    /// <summary>The ids of the labels, creating a label the repository does not have yet (the factory's own labels, such as "incident", may be missing).</summary>
    private async Task<List<long>> LabelIdsAsync(string owner, string repo, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        if (names.Count == 0)
        {
            return ids;
        }
        using var listed = await http.GetAsync($"{RepoPath(owner, repo)}/labels?limit=100", cancellationToken);
        await EnsureAsync(listed, "label list", cancellationToken);
        using var labels = await JsonDocument.ParseAsync(await listed.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        foreach (var name in names)
        {
            var match = labels.RootElement.ValueKind == JsonValueKind.Array
                ? labels.RootElement.EnumerateArray().FirstOrDefault(label => label.TryGetProperty("name", out var n) && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                : default;
            if (match.ValueKind == JsonValueKind.Object && match.TryGetProperty("id", out var id))
            {
                ids.Add(id.GetInt64());
                continue;
            }
            using var created = await http.PostAsJsonAsync($"{RepoPath(owner, repo)}/labels", new { name, color = NewLabelColour }, cancellationToken);
            await EnsureAsync(created, "label", cancellationToken);
            using var document = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            ids.Add(document.RootElement.GetProperty("id").GetInt64());
        }
        return ids;
    }

    private static Task EnsureAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            // Only the status: the body may echo what we sent.
            throw new HttpRequestException($"Forgejo refused the {what} request: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        return Task.CompletedTask;
    }
}
