namespace AiSdlcFactory.Controller;

/// <summary>One git host the factory serves, as the controller needs it: where it is, which kind it is (<c>forgejo</c>, <c>github</c>), and the bot's token and login on it, how its tickets say which workflow to run, and who may steer the factory on its repositories.</summary>
public sealed record HostSettings(string Name, string Type, string Url, string Token, string BotLogin, TriggerRules? Triggers = null, HostCommandRules? Commands = null)
{
    /// <summary>Where the host's API is: what its kind makes of its address (<see cref="GitHostKinds"/>).</summary>
    public string ApiBase => GitHostKinds.For(Type).ApiBase(Url);

    /// <summary>The header git is given to clone and push over HTTPS with the bot's token, in the way its kind takes it; empty without a token.</summary>
    public string GitAuthorization => Token.Length == 0 ? "" : GitHostKinds.For(Type).GitAuthorization(Token);

    /// <summary>The client for this host's API.</summary>
    public IGitHost CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(ApiBase + "/"), Timeout = TimeSpan.FromSeconds(20) };
        return GitHostKinds.For(Type).CreateClient(http, Token);
    }
}
