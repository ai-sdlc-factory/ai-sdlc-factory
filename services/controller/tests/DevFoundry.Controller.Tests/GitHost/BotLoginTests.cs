using Microsoft.Extensions.Configuration;

namespace DevFoundry.Controller.Tests;

public sealed class BotLoginSettingTests
{
    private static WorkerSettings Settings(string host) =>
        WorkerSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FACTORY_HOSTS"] = $"[{host}]" }).Build());

    [Fact]
    public void The_bot_is_called_factory_bot_unless_configured_otherwise() =>
        Assert.Equal("factory-bot", Settings("""{"name":"forgejo","url":"http://forgejo:3000"}""").BotLoginFor("acme"));

    [Fact]
    public void The_bots_login_can_be_set() =>
        Assert.Equal("my-factory", Settings("""{"name":"forgejo","url":"http://forgejo:3000","bot_login":"my-factory"}""").BotLoginFor("acme"));

    [Fact]
    public void An_empty_setting_falls_back_to_the_default() =>
        Assert.Equal("factory-bot", Settings("""{"name":"forgejo","url":"http://forgejo:3000","bot_login":"  "}""").BotLoginFor("acme"));
}
