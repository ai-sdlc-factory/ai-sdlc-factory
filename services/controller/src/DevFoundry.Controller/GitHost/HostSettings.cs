namespace DevFoundry.Controller;

/// <summary>One git host the factory serves, as the controller needs it: where it is, which kind it is (<c>forgejo</c>), and the bot's token and login on it, and how its tickets say which workflow to run.</summary>
public sealed record HostSettings(string Name, string Type, string Url, string Token, string BotLogin, TriggerRules? Triggers = null)
{
    /// <summary>
    /// Where the host's API is. For Forgejo it is the address itself. For GitHub, <c>https://github.com</c> is served by <c>https://api.github.com</c>, and GitHub Enterprise Server
    /// (any other address) by <c>/api/v3</c> under it.
    /// </summary>
    public string ApiBase => Type == GitHubWebhookAdapter.TypeName
        ? (Url.TrimEnd('/').Equals("https://github.com", StringComparison.OrdinalIgnoreCase) ? "https://api.github.com" : Url.TrimEnd('/') + "/api/v3")
        : Url.TrimEnd('/');

    /// <summary>
    /// The header git is given to clone and push over HTTPS with the bot's token. Forgejo takes <c>Authorization: token &lt;token&gt;</c>; GitHub takes HTTP Basic with the token as the
    /// password (the user name is not checked).
    /// </summary>
    public string GitAuthorization => Token.Length == 0
        ? ""
        : Type == GitHubWebhookAdapter.TypeName
            ? "Authorization: Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x-access-token:" + Token))
            : "Authorization: token " + Token;
}
