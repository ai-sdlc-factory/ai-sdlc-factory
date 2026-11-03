using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Temporalio.Exceptions;

namespace DevFoundry.Controller;

/// <summary>
/// The controller's only route to Forgejo. The token never leaves this process: agents run in
/// containers that have no Forgejo credentials.
/// </summary>
public sealed record PullRequestInfo(int Number, string Url);

public sealed record IssueInfo(int Number, string Url);

public sealed class ForgejoClient(HttpClient http, WorkerSettings settings)
{
    public async Task PostIssueCommentAsync(
        string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ForgejoToken))
        {
            throw new ApplicationFailureException(
                "FORGEJO_BOT_TOKEN is not configured for the controller.", nonRetryable: true);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{issueNumber}/comments")
        {
            Content = JsonContent.Create(new { body = $"{EventRouter.BotMarker}\n{body}" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("token", settings.ForgejoToken);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Client errors will not fix themselves on retry, except rate limiting and timeouts.
        var retryable = (int)response.StatusCode >= 500
            || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
        throw new ApplicationFailureException(
            $"Forgejo rejected the comment: {(int)response.StatusCode} {response.ReasonPhrase}",
            nonRetryable: !retryable);
    }

    /// <summary>Opens a ticket with the given labels (by name). A label that does not exist fails the call without retries.</summary>
    public async Task<IssueInfo> CreateIssueAsync(
        string owner, string repo, string title, string body, IReadOnlyList<string> labelNames, CancellationToken cancellationToken)
    {
        RequireToken();
        var repoPath = $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

        using var list = Authorized(HttpMethod.Get, $"{repoPath}/labels?limit=100");
        using var listed = await http.SendAsync(list, cancellationToken);
        if (!listed.IsSuccessStatusCode)
        {
            throw Failure("label list", listed.StatusCode, listed.ReasonPhrase);
        }
        using var labels = await JsonDocument.ParseAsync(await listed.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var ids = new List<long>();
        foreach (var name in labelNames)
        {
            var match = labels.RootElement.ValueKind == JsonValueKind.Array
                ? labels.RootElement.EnumerateArray().FirstOrDefault(label =>
                    label.TryGetProperty("name", out var n) && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                : default;
            if (match.ValueKind != JsonValueKind.Object || !match.TryGetProperty("id", out var id))
            {
                throw new ApplicationFailureException($"The repository has no \"{name}\" label, so the ticket could not be opened.", nonRetryable: true);
            }
            ids.Add(id.GetInt64());
        }

        using var create = Authorized(HttpMethod.Post, $"{repoPath}/issues");
        create.Content = JsonContent.Create(new { title, body, labels = ids });
        using var response = await http.SendAsync(create, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure("ticket", response.StatusCode, response.ReasonPhrase);
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return new IssueInfo(document.RootElement.GetProperty("number").GetInt32(), document.RootElement.GetProperty("html_url").GetString() ?? "");
    }

    /// <summary>The bodies of a ticket's latest comments, oldest first.</summary>
    public async Task<IReadOnlyList<string>> RecentCommentBodiesAsync(
        string owner, string repo, int issueNumber, int count, CancellationToken cancellationToken)
    {
        RequireToken();
        using var request = Authorized(HttpMethod.Get,
            $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{issueNumber}/comments");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure("comment list", response.StatusCode, response.ReasonPhrase);
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return document.RootElement.EnumerateArray()
            .Select(comment => comment.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "")
            .TakeLast(count)
            .ToList();
    }

    /// <summary>
    /// Posts a notice unless one of the ticket's latest comments is already the same kind of notice, so a string of
    /// comments gets one reply, not one each. Returns whether it posted.
    /// </summary>
    public async Task<bool> PostNoticeOnceAsync(
        string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken)
    {
        var recent = await RecentCommentBodiesAsync(owner, repo, issueNumber, 10, cancellationToken);
        if (recent.Any(comment => comment.Contains(TicketComments.NoticeMarker, StringComparison.Ordinal)))
        {
            return false;
        }
        await PostIssueCommentAsync(owner, repo, issueNumber, $"{TicketComments.NoticeMarker}\n{body}", cancellationToken);
        return true;
    }

    /// <summary>
    /// Opens a pull request for a pushed branch. If one already exists for the branch (a retried attempt
    /// after the first one succeeded), that one is returned instead of failing.
    /// </summary>
    public async Task<PullRequestInfo> CreatePullRequestAsync(
        string owner, string repo, string head, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        RequireToken();
        var repoPath = $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

        using var create = Authorized(HttpMethod.Post, $"{repoPath}/pulls");
        create.Content = JsonContent.Create(new { head, @base = baseBranch, title, body });
        using var response = await http.SendAsync(create, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await ReadPullRequestAsync(response, cancellationToken);
        }
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            using var existing = Authorized(HttpMethod.Get,
                $"{repoPath}/pulls/{Uri.EscapeDataString(baseBranch)}/{Uri.EscapeDataString(head)}");
            using var found = await http.SendAsync(existing, cancellationToken);
            if (found.IsSuccessStatusCode)
            {
                return await ReadPullRequestAsync(found, cancellationToken);
            }
        }
        throw Failure("pull request", response.StatusCode, response.ReasonPhrase);
    }

    private static async Task<PullRequestInfo> ReadPullRequestAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        return new PullRequestInfo(root.GetProperty("number").GetInt32(), root.GetProperty("html_url").GetString() ?? "");
    }

    private void RequireToken()
    {
        if (string.IsNullOrWhiteSpace(settings.ForgejoToken))
        {
            throw new ApplicationFailureException("FORGEJO_BOT_TOKEN is not configured for the controller.", nonRetryable: true);
        }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", settings.ForgejoToken);
        return request;
    }

    // Client errors will not fix themselves on retry, except rate limiting and timeouts.
    private static ApplicationFailureException Failure(string what, HttpStatusCode status, string? reason)
    {
        var retryable = (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
        return new ApplicationFailureException($"Forgejo rejected the {what}: {(int)status} {reason}", nonRetryable: !retryable);
    }
}
