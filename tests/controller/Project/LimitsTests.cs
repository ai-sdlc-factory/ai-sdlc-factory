using Microsoft.Extensions.Configuration;
using Xunit;

namespace DevFoundry.Controller.Tests;

public sealed class LimitsTests
{
    private static string Json(string extra) => $$"""{"image":"python","gates":[{"name":"test","command":["pytest"]}],{{extra}}}""";

    private static ProjectConfig Parse(string extra)
    {
        Assert.True(ProjectConfigParser.TryParse(Json(extra), out var config, out var error), error);
        return config!;
    }

    private static string ParseError(string extra)
    {
        Assert.False(ProjectConfigParser.TryParse(Json(extra), out _, out var error));
        return error;
    }

    [Fact]
    public void Limits_are_read_and_missing_ones_fall_back_to_the_factorys_defaults()
    {
        var config = Parse("""
            "limits":{"build_rounds":5,"clarification_rounds":0}
            """);

        var effective = config.Limits!.Resolve(new FactoryDefaults(2, 3, 4));

        Assert.Equal(new EffectiveLimits(5, 0, 4), effective);
    }

    [Fact]
    public void A_project_without_limits_gets_the_defaults_as_they_are()
    {
        Assert.True(ProjectConfigParser.TryParse("""{"image":"python","gates":[{"name":"test","command":["pytest"]}]}""", out var config, out _));

        Assert.Equal(new EffectiveLimits(2, 1, 6), (config!.Limits ?? new ProjectLimits()).Resolve(new FactoryDefaults(2, 1, 6)));
    }

    [Theory]
    [InlineData("build_rounds", "0")]
    [InlineData("build_rounds", "6")]
    [InlineData("clarification_rounds", "-1")]
    [InlineData("clarification_rounds", "5")]
    [InlineData("revisions_per_pull_request", "0")]
    [InlineData("revisions_per_pull_request", "11")]
    [InlineData("build_rounds", "\"3\"")]
    [InlineData("build_rounds", "2.5")]
    public void A_value_outside_its_range_is_refused_with_the_range(string key, string value)
    {
        var error = ParseError($$""" "limits":{"{{key}}":{{value}}} """);

        Assert.Contains($"limits.{key}", error);
        Assert.Contains("from", error);
    }

    [Fact]
    public void A_misspelled_limit_is_refused_instead_of_ignored()
    {
        Assert.Contains("build_round\"", ParseError(""" "limits":{"build_round":2} """));
    }

    [Fact]
    public void Limits_must_be_an_object()
    {
        Assert.Contains("must be an object", ParseError(""" "limits":3 """));
    }

    [Fact]
    public void Protected_paths_are_read_and_duplicates_dropped()
    {
        var config = Parse(""" "protected_paths":["db/migrations/**","*.lock","*.lock"] """);

        Assert.Equal(["db/migrations/**", "*.lock"], config.ProtectedPaths);
    }

    [Theory]
    [InlineData("""["/etc/*"]""")]
    [InlineData("""["../x"]""")]
    [InlineData("""["a b"]""")]
    [InlineData("""[3]""")]
    [InlineData("\"x\"")]
    public void A_protected_path_that_is_not_a_pattern_is_refused(string value) =>
        Assert.Contains("protected", ParseError($$""" "protected_paths":{{value}} """));

    [Fact]
    public void Too_many_protected_paths_are_refused()
    {
        var many = "[" + string.Join(',', Enumerable.Range(1, 21).Select(i => $"\"p{i}/**\"")) + "]";

        Assert.Contains("at most 20", ParseError($$""" "protected_paths":{{many}} """));
    }

    [Fact]
    public void Project_patterns_only_add_to_the_built_in_protection()
    {
        Assert.True(ProtectedPaths.IsProtected(".factory/project.json", ["db/**"]));
        Assert.True(ProtectedPaths.IsProtected("DB/Migrations/1.sql", ["db/migrations/**"]));
        Assert.True(ProtectedPaths.IsProtected("sub/deps.LOCK", ["*.lock"]));
        Assert.False(ProtectedPaths.IsProtected("src/app.py", ["db/migrations/**"]));
        Assert.False(ProtectedPaths.IsProtected("src/app.py"));
    }

    private static WorkerSettings Settings(params (string Key, string Value)[] values) =>
        WorkerSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(TestHosts.Setting).AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

    [Fact]
    public void The_factorys_defaults_are_three_three_and_three_unless_configured()
    {
        Assert.Equal(new FactoryDefaults(3, 3, 3), Settings().Defaults);
        Assert.Equal(
            new FactoryDefaults(5, 0, 10),
            Settings(("FACTORY_DEFAULT_BUILD_ROUNDS", "5"), ("FACTORY_DEFAULT_CLARIFICATION_ROUNDS", "0"), ("FACTORY_DEFAULT_REVISIONS_PER_PULL_REQUEST", "10")).Defaults);
        Assert.Equal(FactoryDefaults.Standard, Settings(("FACTORY_DEFAULT_BUILD_ROUNDS", "  ")).Defaults);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void Repository_images_are_off_unless_the_operator_says_true(string? value, bool expected)
    {
        var settings = value is null ? Settings() : Settings(("FACTORY_ALLOW_REPO_IMAGES", value));

        Assert.Equal(expected, settings.AllowRepoImages);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("1")]
    [InlineData("yes")]
    public void A_repository_images_switch_that_is_not_true_or_false_stops_the_start(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Settings(("FACTORY_ALLOW_REPO_IMAGES", value)));

        Assert.Contains("FACTORY_ALLOW_REPO_IMAGES must be true or false", error.Message);
    }

    [Theory]
    [InlineData("FACTORY_DEFAULT_BUILD_ROUNDS", "0")]
    [InlineData("FACTORY_DEFAULT_BUILD_ROUNDS", "9")]
    [InlineData("FACTORY_DEFAULT_CLARIFICATION_ROUNDS", "5")]
    [InlineData("FACTORY_DEFAULT_REVISIONS_PER_PULL_REQUEST", "abc")]
    [InlineData("FACTORY_DEFAULT_REVISIONS_PER_PULL_REQUEST", "-2")]
    public void A_default_outside_its_range_stops_the_start_with_the_name_of_the_setting(string key, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Settings((key, value)));

        Assert.Contains(key, error.Message);
    }
}
