using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Temporalio.Exceptions;

namespace DevFoundry.Controller;

/// <summary>
/// The controller's only route to Forgejo. The token never leaves this process: agents run in
/// containers that have no Forgejo credentials.
/// </summary>
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
}
