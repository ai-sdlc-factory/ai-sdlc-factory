using Microsoft.Extensions.Configuration;

namespace AiSdlcFactory.Controller.Tests;

/// <summary>A role's model has to be on a provider agents/opencode.json enables, or the factory does not start; and nothing is said where it cannot be known.</summary>
public sealed class ModelProvidersTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("agents-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private void OpenCode(string json) => File.WriteAllText($"{directory}/opencode.json", json);

    private static IReadOnlyDictionary<string, string> Roles(string role = "reviewer", string model = "anthropic/claude-sonnet-5-5") => new Dictionary<string, string> { [role] = model };

    [Fact]
    public void The_providers_are_read_from_the_agents_folder_with_comments_and_trailing_commas_allowed()
    {
        OpenCode("""
            { // which providers may be used
              "enabled_providers": ["openai", "anthropic",], }
            """);

        Assert.Equal(["openai", "anthropic"], ModelProviders.Enabled(directory));
    }

    [Fact]
    public void A_model_on_an_enabled_provider_is_accepted()
    {
        OpenCode("""{"enabled_providers": ["openai", "anthropic"]}""");

        ModelProviders.RequireEnabled(Roles(), directory);
        ModelProviders.RequireEnabled(Roles("security", "openai/gpt-6.1-sol-fast"), directory);
    }

    [Fact]
    public void A_model_on_a_provider_that_is_not_enabled_stops_the_start_and_says_what_to_change()
    {
        OpenCode("""{"enabled_providers": ["openai"]}""");

        var message = Assert.Throws<InvalidOperationException>(() => ModelProviders.RequireEnabled(Roles(), directory)).Message;

        Assert.Contains("FACTORY_ROLE_MODELS", message);
        Assert.Contains("\"reviewer\"", message);
        Assert.Contains("anthropic/claude-sonnet-5-5", message);
        Assert.Contains("\"anthropic\"", message);
        Assert.Contains("enabled_providers: openai", message);
        Assert.Contains("docs/opencode-login.md", message);
    }

    [Fact]
    public void An_empty_list_enables_nothing_and_says_so()
    {
        OpenCode("""{"enabled_providers": []}""");

        Assert.Contains("enabled_providers: none", Assert.Throws<InvalidOperationException>(() => ModelProviders.RequireEnabled(Roles(), directory)).Message);
    }

    [Theory]
    [InlineData(null)]                                    // no folder known
    [InlineData("missing")]                               // no opencode.json in it
    public void Where_there_is_no_file_nothing_is_said(string? kind)
    {
        ModelProviders.RequireEnabled(Roles(), kind is null ? null : directory);
        Assert.Null(ModelProviders.Enabled(kind is null ? null : directory));
    }

    [Theory]
    [InlineData("{}")]                                    // no list: nothing restricts the providers
    [InlineData("""{"enabled_providers": "openai"}""")]   // not a list
    [InlineData("not json")]                              // cannot be read
    [InlineData("[]")]                                    // not an object
    public void A_file_without_a_usable_list_says_nothing_and_never_fails_the_start(string json)
    {
        OpenCode(json);

        ModelProviders.RequireEnabled(Roles(), directory);
        Assert.Null(ModelProviders.Enabled(directory));
    }

    [Fact]
    public void No_role_models_means_nothing_to_check_even_when_nothing_is_enabled()
    {
        OpenCode("""{"enabled_providers": []}""");

        ModelProviders.RequireEnabled(new Dictionary<string, string>(), directory);
    }

    [Fact]
    public void The_controller_stops_at_start_for_a_role_model_on_a_provider_that_is_not_enabled_and_starts_once_it_is()
    {
        WorkerSettings Load() => WorkerSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(TestHosts.Setting).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FACTORY_ROLE_MODELS"] = """{"reviewer":"anthropic/claude-sonnet-5-5"}""",
            ["FACTORY_AGENTS_DIR"] = directory,
        }).Build());

        OpenCode("""{"enabled_providers": ["openai"]}""");
        Assert.Contains("does not enable", Assert.Throws<InvalidOperationException>(Load).Message);

        OpenCode("""{"enabled_providers": ["openai", "anthropic"]}""");
        Assert.Equal("anthropic/claude-sonnet-5-5", Load().RoleModels["reviewer"]);
    }

    [Fact]
    public void The_shipped_agents_folder_enables_the_provider_of_its_default_model()
    {
        var shipped = AppContext.BaseDirectory;
        for (var folder = new DirectoryInfo(shipped); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "agents", "opencode.json")))
            {
                Assert.Contains("openai", ModelProviders.Enabled(Path.Combine(folder.FullName, "agents"))!);
                return;
            }
        }
    }
}
