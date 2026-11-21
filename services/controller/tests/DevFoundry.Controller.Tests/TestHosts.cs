namespace DevFoundry.Controller.Tests;

/// <summary>The hosts most tests need: one Forgejo called <c>forgejo</c> (whose owners stay plain), with the bot's token.</summary>
internal static class TestHosts
{
    /// <summary>The same host as configuration, for tests that read settings from the environment.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> Setting { get; } = [new("FACTORY_HOSTS", """[{"name":"forgejo","url":"http://forgejo:3000","token":"t"}]""")];

    public static IReadOnlyList<HostSettings> Forgejo(string token = "") => [new HostSettings("forgejo", "forgejo", "http://forgejo:3000", token, "factory-bot")];
}
