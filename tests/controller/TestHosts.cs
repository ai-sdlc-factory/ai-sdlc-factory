namespace DevFoundry.Controller.Tests;

/// <summary>The host most tests need: one git host with the bot's token. It is called <c>forgejo</c> because that is the name whose owners stay plain (<c>acme</c>, not <c>host:acme</c>).</summary>
internal static class TestHosts
{
    /// <summary>The same host as configuration, for tests that read settings from the environment.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> Setting { get; } = [new("FACTORY_HOSTS", """[{"name":"forgejo","url":"http://forgejo:3000","token":"t"}]""")];

    public static IReadOnlyList<HostSettings> One(string token = "") => [new HostSettings("forgejo", "forgejo", "http://forgejo:3000", token, "factory-bot")];
}
