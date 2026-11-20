namespace DevFoundry.Controller;

/// <summary>One git host the factory serves, as the controller needs it: where it is, which kind it is (<c>forgejo</c>), and the bot's token and login on it.</summary>
public sealed record HostSettings(string Name, string Type, string Url, string Token, string BotLogin);
