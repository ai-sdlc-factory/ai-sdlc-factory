using System.Net.Http.Json;
using System.Text.Json;

namespace DevFoundry.AlertIntake;

/// <summary>The tickets of a GitHub host (github.com or a GitHub Enterprise Server), over its REST API. The labels of a new ticket are made by GitHub when the repository has not got them.</summary>
public sealed class GitHubClient(HttpClient http) : RestTicketHost(http, "GitHub")
{
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

    protected override string RepoPath(string owner, string repo) => $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    protected override string ListQuery(int page) => $"state=open&per_page={PageSize}&page={page}";

    /// <summary>GitHub lists pull requests among the issues; they are skipped.</summary>
    protected override bool IsTicket(JsonElement issue) => !issue.TryGetProperty("pull_request", out _);

    public override async Task<IssueRef> CreateTicketAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        using var response = await Http.PostAsJsonAsync($"{RepoPath(owner, repo)}/issues", new { title, body, labels }, cancellationToken);
        await EnsureAsync(response, "ticket");
        return await ReadIssueAsync(response, cancellationToken);
    }
}
