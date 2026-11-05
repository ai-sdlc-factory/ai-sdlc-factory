using Microsoft.Extensions.Configuration;

namespace DevFoundry.Controller.Tests;

public sealed class BotLoginSettingTests
{
    private static WorkerSettings Settings(params (string Key, string? Value)[] values) =>
        WorkerSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

    [Fact]
    public void The_bot_is_called_factory_bot_unless_configured_otherwise() =>
        Assert.Equal("factory-bot", Settings().ForgejoBotLogin);

    [Fact]
    public void The_bots_login_can_be_set() =>
        Assert.Equal("my-factory", Settings(("FORGEJO_BOT_LOGIN", "my-factory")).ForgejoBotLogin);

    [Fact]
    public void An_empty_setting_falls_back_to_the_default() =>
        Assert.Equal("factory-bot", Settings(("FORGEJO_BOT_LOGIN", "  ")).ForgejoBotLogin);
}
