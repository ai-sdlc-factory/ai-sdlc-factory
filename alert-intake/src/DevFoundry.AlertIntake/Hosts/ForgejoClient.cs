using System.Net.Http.Json;
using System.Text.Json;

namespace DevFoundry.AlertIntake;

/// <summary>The tickets of a Forgejo (and Gitea) host, over its REST API.</summary>
public sealed class ForgejoClient(HttpClient http) : RestTicketHost(http, "Forgejo")
{
    private const string NewLabelColour = "#d73a4a";

    /// <summary>Where the REST API is: the address of the host itself.</summary>
    public static string ApiBase(string url) => url;

    /// <summary>Gives a client the header Forgejo asks for.</summary>
    public static void Prepare(HttpClient client, string token)
    {
        if (token.Length > 0)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", token);
        }
    }

    protected override string RepoPath(string owner, string repo) => $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    protected override string ListQuery(int page) => $"state=open&type=issues&limit={PageSize}&page={page}";

    public override async Task<IssueRef> CreateTicketAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        var ids = await LabelIdsAsync(owner, repo, labels, cancellationToken);
        using var response = await Http.PostAsJsonAsync($"{RepoPath(owner, repo)}/issues", new { title, body, labels = ids }, cancellationToken);
        await EnsureAsync(response, "ticket");
        return await ReadIssueAsync(response, cancellationToken);
    }

    /// <summary>The ids of the labels, creating a label the repository does not have yet (the factory's own labels, such as "incident", may be missing).</summary>
    private async Task<List<long>> LabelIdsAsync(string owner, string repo, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        if (names.Count == 0)
        {
            return ids;
        }
        using var listed = await Http.GetAsync($"{RepoPath(owner, repo)}/labels?limit=100", cancellationToken);
        await EnsureAsync(listed, "label list");
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
            using var created = await Http.PostAsJsonAsync($"{RepoPath(owner, repo)}/labels", new { name, color = NewLabelColour }, cancellationToken);
            await EnsureAsync(created, "label");
            using var document = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            ids.Add(document.RootElement.GetProperty("id").GetInt64());
        }
        return ids;
    }
}
