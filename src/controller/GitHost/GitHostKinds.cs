namespace DevFoundry.Controller;

/// <summary>
/// One kind of git host the controller can serve: how to reach its API, how git signs in to it, and the client that speaks its API. A new kind (GitLab, ...) is a client class, a webhook
/// adapter in the contracts library and one entry in <see cref="GitHostKinds.All"/>; nothing else asks which kind a host is.
/// </summary>
public sealed record GitHostKind(
    string Type,
    Func<string, string> ApiBase,
    Func<string, string> GitAuthorization,
    Func<HttpClient, string, IGitHost> CreateClient);

public static class GitHostKinds
{
    public static readonly IReadOnlyList<GitHostKind> All =
    [
        new(ForgejoWebhookAdapter.TypeName, ForgejoClient.ApiBase, ForgejoClient.GitAuthorization, (http, token) => new ForgejoClient(http, token)),
        new(GitHubWebhookAdapter.TypeName, GitHubClient.ApiBase, GitHubClient.GitAuthorization, (http, token) => new GitHubClient(http, token)),
    ];

    /// <summary>The kind with this type, which every host of the settings has: the type was checked against the webhook adapters when the settings were read.</summary>
    public static GitHostKind For(string type) =>
        All.FirstOrDefault(kind => kind.Type == type) ?? throw new InvalidOperationException($"No client for the git host type \"{type}\".");
}
