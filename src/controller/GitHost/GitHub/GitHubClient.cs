using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Temporalio.Exceptions;
using static AiSdlcFactory.Controller.HostJson;

namespace AiSdlcFactory.Controller;

/// <summary>
/// The controller's route to GitHub (github.com or GitHub Enterprise Server): an <see cref="IGitHost"/> over the REST API, for one host. The token never leaves this process.
/// It does what the Forgejo client does, in GitHub's words; the differences are noted where they are. Not every call has a GitHub equivalent:
/// conversations marked resolved are not visible to the REST API (only to GraphQL), so a review comment in a resolved conversation is still given to the revision.
/// </summary>
public sealed class GitHubClient(HttpClient http, string token) : IGitHost
{
    /// <summary>Where the REST API is: <c>https://api.github.com</c> for github.com, <c>/api/v3</c> under the address of a GitHub Enterprise Server.</summary>
    public static string ApiBase(string url) =>
        url.TrimEnd('/').Equals("https://github.com", StringComparison.OrdinalIgnoreCase) ? "https://api.github.com" : url.TrimEnd('/') + "/api/v3";

    /// <summary>The header git signs in with over HTTPS: HTTP Basic with the token as the password (the user name is not checked).</summary>
    public static string GitAuthorization(string token) =>
        "Authorization: Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x-access-token:" + token));

    private const int PerPage = 100;
    private const int MaxPages = 20;
    private const int MaxFilePages = 30; // the files endpoint lists at most 3,000 files

    // ---- comments and tickets

    public async Task PostIssueCommentAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken)
    {
        RequireToken();
        using var request = Authorized(HttpMethod.Post, $"{RepoPath(owner, repo)}/issues/{issueNumber}/comments");
        request.Content = JsonContent.Create(new { body = $"{EventRouter.BotMarker}\n{body}" });
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure("comment", response);
        }
    }

    public async Task<IReadOnlyList<string>> RecentCommentBodiesAsync(string owner, string repo, int issueNumber, int count, CancellationToken cancellationToken)
    {
        var bodies = new List<string>();
        await foreach (var comment in PagedAsync($"{RepoPath(owner, repo)}/issues/{issueNumber}/comments", "comment list", cancellationToken))
        {
            bodies.Add(Text(comment, "body"));
        }
        return bodies.TakeLast(count).ToList();
    }

    public async Task<bool> PostNoticeOnceAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken)
    {
        var recent = await RecentCommentBodiesAsync(owner, repo, issueNumber, 10, cancellationToken);
        if (recent.Any(comment => comment.Contains(TicketComments.NoticeMarker, StringComparison.Ordinal)))
        {
            return false;
        }
        await PostIssueCommentAsync(owner, repo, issueNumber, $"{TicketComments.NoticeMarker}\n{body}", cancellationToken);
        return true;
    }

    public async Task<bool> PostNoticeUniqueAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken)
    {
        var comments = await RecentCommentBodiesAsync(owner, repo, issueNumber, 200, cancellationToken);
        if (comments.Any(comment => comment.Contains(body, StringComparison.Ordinal)))
        {
            return false;
        }
        await PostIssueCommentAsync(owner, repo, issueNumber, $"{TicketComments.NoticeMarker}\n{body}", cancellationToken);
        return true;
    }

    /// <summary>Opens a ticket with these labels (by name). GitHub creates a label that does not exist yet when the token may push; otherwise it leaves the label out.</summary>
    public async Task<IssueInfo> CreateIssueAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labelNames, CancellationToken cancellationToken)
    {
        RequireToken();
        using var request = Authorized(HttpMethod.Post, $"{RepoPath(owner, repo)}/issues");
        request.Content = JsonContent.Create(new { title, body, labels = labelNames });
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure("ticket", response);
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return new IssueInfo(document.RootElement.GetProperty("number").GetInt32(), Text(document.RootElement, "html_url"));
    }

    // ---- pull requests

    public async Task<PullRequestDetails> GetPullRequestAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        RequireToken();
        using var request = Authorized(HttpMethod.Get, $"{RepoPath(owner, repo)}/pulls/{number}");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure("pull request", response);
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return PullRequest(document.RootElement, number);
    }

    /// <summary>The files a pull request changes (the first <see cref="GitHostLimits.MaxChangedFiles"/>, with the total) and the start of its diff. A diff GitHub will not give (too large) comes back empty and cut.</summary>
    public async Task<PullChanges> GetPullChangesAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        RequireToken();
        var path = $"{RepoPath(owner, repo)}/pulls/{number}";
        var files = new List<ChangedFile>();
        var total = 0;
        for (var page = 1; page <= MaxFilePages; page++)
        {
            using var request = Authorized(HttpMethod.Get, $"{path}/files?per_page={PerPage}&page={page}");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure("changed files", response);
            }
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }
            var listed = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                listed++;
                // The factory's own run record files are not part of what the pull request changes.
                if (RunRecords.IsRunRecord(Text(item, "filename")))
                {
                    continue;
                }
                total++;
                if (files.Count < GitHostLimits.MaxChangedFiles)
                {
                    files.Add(ChangedFile(item, FileStatus));
                }
            }
            if (listed < PerPage)
            {
                break;
            }
        }

        using var diffRequest = Authorized(HttpMethod.Get, path);
        diffRequest.Headers.Accept.Clear();
        diffRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.diff"));
        using var diffResponse = await http.SendAsync(diffRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (diffResponse.StatusCode is HttpStatusCode.NotAcceptable or HttpStatusCode.UnprocessableEntity)
        {
            return new PullChanges(files, total, "", true); // "diff too large": the file list still says what changed
        }
        if (!diffResponse.IsSuccessStatusCode)
        {
            throw Failure("diff", diffResponse);
        }
        return await DiffReader.ReadAsync(diffResponse, files, total, cancellationToken);
    }

    /// <summary>GitHub's file statuses in the factory's words: a removed file is deleted; a copied or changed one is modified.</summary>
    private static string FileStatus(string status) => status switch
    {
        "added" => "added",
        "removed" => "deleted",
        "renamed" => "renamed",
        _ => "modified",
    };


    /// <summary>
    /// Every review still waiting to be acted on: by a person (not the bot), asking for changes or commenting (an approval is not a request; a dismissed review is not listed as one), and submitted
    /// after the factory's last revision. GitHub's states are given in the factory's words (<c>REQUEST_CHANGES</c>, <c>COMMENT</c>). Oldest first.
    /// </summary>
    public async Task<IReadOnlyList<ReviewDetails>> GetUnaddressedReviewsAsync(string owner, string repo, int number, string botLogin, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        RequireToken();
        var path = $"{RepoPath(owner, repo)}/pulls/{number}/reviews";
        var chosen = new List<JsonElement>();
        await foreach (var review in PagedAsync(path, "review list", cancellationToken))
        {
            if (review.TryGetProperty("id", out _)
                && Text(review, "state") is "CHANGES_REQUESTED" or "COMMENTED"
                && !string.Equals(Login(review), botLogin, StringComparison.OrdinalIgnoreCase)
                && (since is null || When(review, "submitted_at") is not { } submitted || submitted > since))
            {
                chosen.Add(review);
            }
        }

        var result = new List<ReviewDetails>();
        foreach (var review in chosen.OrderBy(review => review.GetProperty("id").GetInt64()))
        {
            var id = review.GetProperty("id").GetInt64();
            var comments = new List<ReviewComment>();
            await foreach (var comment in PagedAsync($"{path}/{id}/comments", "review comments", cancellationToken, tolerateFailure: true))
            {
                comments.Add(ReviewComment(comment, "line", "original_line", "position"));
            }
            var body = Text(review, "body");
            if (comments.Count == 0 && string.IsNullOrWhiteSpace(body))
            {
                continue; // nothing left to act on
            }
            result.Add(new ReviewDetails(id, Login(review), Text(review, "state") == "CHANGES_REQUESTED" ? "REQUEST_CHANGES" : "COMMENT", body, comments));
        }
        return result;
    }

    public async Task<RevisionHistory> GetRevisionHistoryAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        var count = 0;
        DateTimeOffset? last = null;
        await foreach (var comment in PagedAsync($"{RepoPath(owner, repo)}/issues/{number}/comments", "comment list", cancellationToken))
        {
            if (!Text(comment, "body").Contains(RevisionReply.Marker, StringComparison.Ordinal))
            {
                continue;
            }
            count++;
            if (When(comment, "created_at") is { } at && (last is null || at > last))
            {
                last = at;
            }
        }
        return new RevisionHistory(count, last);
    }

    /// <summary>Asks people to look at a pull request again. Best effort: a refusal (the author cannot be asked, the token may not) is not an error; only a server error is.</summary>
    public async Task<bool> RequestReviewAsync(string owner, string repo, int number, IReadOnlyList<string> reviewers, CancellationToken cancellationToken)
    {
        RequireToken();
        if (reviewers.Count == 0)
        {
            return false;
        }
        using var request = Authorized(HttpMethod.Post, $"{RepoPath(owner, repo)}/pulls/{number}/requested_reviewers");
        request.Content = JsonContent.Create(new { reviewers });
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }
        if ((int)response.StatusCode >= 500)
        {
            throw Failure("review request", response);
        }
        return false;
    }

    public async Task<int?> FindFactoryPullRequestAsync(string owner, string repo, int ticket, CancellationToken cancellationToken)
    {
        var branch = new Regex($@"\Afactory/issue-{ticket}(?:-\d+)?\z");
        int? found = null;
        await foreach (var pull in PagedAsync($"{RepoPath(owner, repo)}/pulls?state=open", "pull request list", cancellationToken))
        {
            if (branch.IsMatch(Text(Child(pull, "head"), "ref")) && pull.TryGetProperty("number", out var number) && number.TryGetInt32(out var value))
            {
                found = Math.Max(found ?? 0, value);
            }
        }
        return found;
    }

    /// <summary>Every pull request, open or not: numbers, states and dates only. GitHub's list has no <c>merged</c> flag; a pull request with a <c>merged_at</c> was merged.</summary>
    public async Task<IReadOnlyList<PullSummary>> ListAllPullRequestsAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var result = new List<PullSummary>();
        await foreach (var pull in PagedAsync($"{RepoPath(owner, repo)}/pulls?state=all", "pull request list", cancellationToken))
        {
            if (!pull.TryGetProperty("number", out var number) || !number.TryGetInt32(out var value) || When(pull, "created_at") is not { } created)
            {
                continue;
            }
            var mergedAt = When(pull, "merged_at");
            result.Add(new PullSummary(value, Text(pull, "state"), mergedAt is not null, Text(Child(pull, "head"), "ref"), Text(Child(pull, "user"), "login"), created, mergedAt));
        }
        return result;
    }

    /// <summary>Opens a pull request from a pushed branch. When one already exists for the branch (a retried attempt after the first succeeded), that one is returned.</summary>
    public async Task<PullRequestInfo> CreatePullRequestAsync(string owner, string repo, string head, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        RequireToken();
        using var create = Authorized(HttpMethod.Post, $"{RepoPath(owner, repo)}/pulls");
        create.Content = JsonContent.Create(new { head, @base = baseBranch, title, body });
        using var response = await http.SendAsync(create, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await ReadPullRequestAsync(response, cancellationToken);
        }
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            using var existing = Authorized(HttpMethod.Get, $"{RepoPath(owner, repo)}/pulls?state=open&head={Uri.EscapeDataString($"{owner}:{head}")}");
            using var found = await http.SendAsync(existing, cancellationToken);
            if (found.IsSuccessStatusCode)
            {
                using var document = await JsonDocument.ParseAsync(await found.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0)
                {
                    var pull = document.RootElement[0];
                    return new PullRequestInfo(pull.GetProperty("number").GetInt32(), Text(pull, "html_url"));
                }
            }
        }
        throw Failure("pull request", response);
    }

    private static async Task<PullRequestInfo> ReadPullRequestAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return new PullRequestInfo(document.RootElement.GetProperty("number").GetInt32(), Text(document.RootElement, "html_url"));
    }

    // ---- the janitor's five

    /// <summary>Repositories the token can push to: the only ones the factory makes branches in.</summary>
    public async Task<IReadOnlyList<(string Owner, string Repo)>> ListWritableRepositoriesAsync(CancellationToken cancellationToken)
    {
        var result = new List<(string, string)>();
        await foreach (var repository in PagedAsync("user/repos?affiliation=owner,collaborator,organization_member", "repository list", cancellationToken))
        {
            var canPush = Child(repository, "permissions") is { ValueKind: JsonValueKind.Object } permissions
                && permissions.TryGetProperty("push", out var push) && push.ValueKind == JsonValueKind.True;
            if (canPush && Text(repository, "full_name").Split('/') is [var owner, var repo])
            {
                result.Add((owner, repo));
            }
        }
        return result;
    }

    /// <summary>
    /// The factory's work-in-progress branches with the date of their last commit. GitHub's branch list carries no date, and dating a branch costs a request, so only the branches
    /// the janitor looks at (<see cref="JanitorRules.WipIssueNumber"/>) are dated and listed.
    /// </summary>
    public async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        RequireToken();
        var result = new List<BranchInfo>();
        await foreach (var branch in PagedAsync($"{RepoPath(owner, repo)}/branches", "branch list", cancellationToken))
        {
            if (Text(branch, "name") is not { Length: > 0 } name || JanitorRules.WipIssueNumber(name) is null)
            {
                continue;
            }
            using var request = Authorized(HttpMethod.Get, $"{RepoPath(owner, repo)}/branches/{Uri.EscapeDataString(name)}");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (When(Child(Child(Child(document.RootElement, "commit"), "commit"), "committer"), "date") is { } at)
            {
                result.Add(new BranchInfo(name, at));
            }
        }
        return result;
    }

    /// <summary>True only when GitHub says the ticket is closed. A ticket it cannot find counts as not closed, so nothing is deleted on a guess.</summary>
    public async Task<bool> IssueIsClosedAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        RequireToken();
        using var request = Authorized(HttpMethod.Get, $"{RepoPath(owner, repo)}/issues/{number}");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw Failure("ticket", response);
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return string.Equals(Text(document.RootElement, "state"), "closed", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlySet<string>> OpenPullRequestBranchesAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var pull in PagedAsync($"{RepoPath(owner, repo)}/pulls?state=open", "pull request list", cancellationToken))
        {
            if (Text(Child(pull, "head"), "ref") is { Length: > 0 } branch)
            {
                result.Add(branch);
            }
        }
        return result;
    }

    /// <summary>Deletes a branch. A branch that is already gone is fine (GitHub answers 422 for a reference that does not exist).</summary>
    public async Task DeleteBranchAsync(string owner, string repo, string branch, CancellationToken cancellationToken)
    {
        RequireToken();
        using var request = Authorized(HttpMethod.Delete, $"{RepoPath(owner, repo)}/git/refs/heads/{string.Join('/', branch.Split('/').Select(Uri.EscapeDataString))}");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity))
        {
            throw Failure("branch deletion", response);
        }
    }

    // ---- plumbing

    private static string RepoPath(string owner, string repo) => $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    /// <summary>The items of a list that GitHub hands out in pages of 100, until a short page (and never more than 20 pages, so nothing keeps us here).</summary>
    private async IAsyncEnumerable<JsonElement> PagedAsync(
        string path, string what, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, bool tolerateFailure = false)
    {
        RequireToken();
        var separator = path.Contains('?') ? '&' : '?';
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = Authorized(HttpMethod.Get, $"{path}{separator}per_page={PerPage}&page={page}");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (tolerateFailure)
                {
                    yield break;
                }
                throw Failure(what, response);
            }
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }
            var count = document.RootElement.GetArrayLength();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                yield return item.Clone(); // the document is disposed when this page is done
            }
            if (count < PerPage)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// A person's access to a repository: <c>collaborators/{login}/permission</c>. Its <c>role_name</c> tells a maintainer (who may write) and a triager (who may only read) apart; the older
    /// <c>permission</c> field is the fallback.
    /// </summary>
    public async Task<HostPermission> GetPermissionAsync(string owner, string repo, string login, CancellationToken cancellationToken)
    {
        RequireToken();
        using var request = Authorized(HttpMethod.Get, $"{RepoPath(owner, repo)}/collaborators/{Uri.EscapeDataString(login)}/permission");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return HostPermission.None;
        }
        if (response.StatusCode == HttpStatusCode.Forbidden && !IsRateLimited(response))
        {
            return HostPermission.Unknown; // the token may not look at other people's access
        }
        if (!response.IsSuccessStatusCode)
        {
            throw Failure("permission lookup", response);
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return Text(document.RootElement, "role_name") switch
        {
            "admin" => HostPermission.Admin,
            "maintain" or "write" => HostPermission.Write,
            "triage" or "read" => HostPermission.Read,
            _ => Text(document.RootElement, "permission") switch
            {
                "admin" => HostPermission.Admin,
                "write" => HostPermission.Write,
                "read" => HostPermission.Read,
                "none" => HostPermission.None,
                _ => HostPermission.Unknown,
            },
        };
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0";

    /// <summary>A small file on the default branch (<c>contents/{path}</c>).</summary>
    public async Task<string?> ReadFileAsync(string owner, string repo, string path, CancellationToken cancellationToken)
    {
        RequireToken();
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        using var request = Authorized(HttpMethod.Get, $"{RepoPath(owner, repo)}/contents/{escaped}");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw Failure("file", response);
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return FileText(document.RootElement);
    }

    private void RequireToken()
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ApplicationFailureException("The GitHub host has no token in FACTORY_HOSTS.", nonRetryable: true);
        }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("ai-sdlc-factory"); // GitHub refuses a request without one
        return request;
    }

    /// <summary>Client errors will not fix themselves on retry, except rate limiting (429, or 403 with no requests left) and timeouts. Only the status is said: a body may echo what was sent.</summary>
    private static ApplicationFailureException Failure(string what, HttpResponseMessage response)
    {
        var status = response.StatusCode;
        var rateLimited = IsRateLimited(response);
        var retryable = (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || rateLimited;
        return new ApplicationFailureException($"GitHub rejected the {what}: {(int)status} {response.ReasonPhrase}", nonRetryable: !retryable);
    }
}
