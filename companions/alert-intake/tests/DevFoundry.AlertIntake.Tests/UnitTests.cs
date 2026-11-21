using Microsoft.Extensions.Configuration;

namespace DevFoundry.AlertIntake.Tests;

public sealed class RedactorTests
{
    [Theory]
    [InlineData("password=hunter2 failed", "password=[redacted] failed")]
    [InlineData("api_key: abc123xyz ok", "api_key: [redacted] ok")]
    [InlineData("token = 'a b c'", "token = [redacted]")]
    [InlineData("Authorization: Bearer abcdefgh12345678", "Authorization: [redacted]")]
    [InlineData("see postgres://app:pw@db/x", "see postgres://[redacted]@db/x")]
    [InlineData("mail alice@example.com now", "mail [email] now")]
    public void Secrets_and_addresses_are_masked(string text, string expected)
    {
        Assert.Equal(expected, Redactor.Mask(text));
    }

    [Fact]
    public void A_long_opaque_token_is_masked_but_a_normal_sentence_is_not()
    {
        Assert.Equal("key [redacted] end", Redactor.Mask("key " + new string('A', 44) + " end"));
        Assert.Equal("the weather api does not answer", Redactor.Mask("the weather api does not answer"));
    }

    [Fact]
    public void A_line_is_one_line_cut_to_length_without_mentions_or_backticks()
    {
        var line = Redactor.Line("a\r\nb `c` @d", 100);

        Assert.Equal("a b 'c' @​d", line);
        Assert.Equal(13, Redactor.Line(string.Join(' ', Enumerable.Repeat("word", 20)), 10).Length); // cut, plus the three dots
        Assert.Equal("[redacted]", Redactor.Line(new string('x', 50), 100)); // a long opaque string is treated as a secret
        Assert.Equal("", Redactor.Line(null, 10));
    }

    [Fact]
    public void A_block_keeps_its_lines_and_drops_control_characters()
    {
        Assert.Equal("one\ntwo", Redactor.Block("one\r\ntwo\u0007 ", 100));
    }
}

public sealed class RouteTableTests
{
    [Fact]
    public void The_first_route_whose_labels_all_match_wins()
    {
        var table = RouteTable.Parse("""
            {"routes":[
              {"match":{"service":"weather","severity":"critical"},"repository":"valters/weather","labels":["incident"]},
              {"match":{"service":"weather"},"repository":"valters/weather-low","labels":["bug"]}]}
            """);

        var critical = table.Find(new Dictionary<string, string> { ["service"] = "weather", ["severity"] = "critical" })!;
        var other = table.Find(new Dictionary<string, string> { ["service"] = "weather", ["severity"] = "warning" })!;

        Assert.Equal(("valters", "weather", "incident"), (critical.Owner, critical.Repo, critical.Labels[0]));
        Assert.Equal("weather-low", other.Repo);
        Assert.Null(table.Find(new Dictionary<string, string> { ["service"] = "shop" }));
        Assert.Equal(2, table.Count);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{"routes":{}}""", "an array of at most")]
    [InlineData("""{"routes":[{"match":{"service":"a"},"repository":"nonsense"}]}""", "owner/name")]
    [InlineData("""{"routes":[{"match":{"service":"a"},"repository":"../x/y"}]}""", "owner/name")]
    [InlineData("""{"routes":[{"match":{},"repository":"a/b"}]}""", "\"match\"")]
    [InlineData("""{"routes":[{"match":{"service":""},"repository":"a/b"}]}""", "non-empty")]
    [InlineData("""{"routes":[{"match":{"service":"a"},"repository":"a/b","labels":["x","y","z","1","2","3"]}]}""", "at most 5")]
    [InlineData("""{"routes":[{"match":{"service":"a"},"repository":"a/b","labels":["bad\nlabel"]}]}""", "plain characters")]
    public void A_routes_file_that_cannot_be_used_is_refused_with_the_reason(string json, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => RouteTable.Parse(json));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_missing_file_is_named()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RouteTable.Load("/no/such/routes.json"));
        Assert.Contains("/no/such/routes.json", error.Message);
    }
}

public sealed class AlertParsingTests
{
    [Fact]
    public void A_message_with_alerts_is_read_and_the_labels_are_data()
    {
        var message = Setup.Parse(Setup.Message(Setup.Firing()));

        var alert = Assert.Single(message.Alerts!);
        Assert.Equal("WeatherApiDown", alert.Name);
        Assert.Equal("critical", alert.LabelMap["severity"]);
        Assert.Equal("http://prometheus:9090/graph?g0.expr=probe_success", alert.GeneratorUrl);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"alerts":[]}""")]
    [InlineData("""{"alerts":[{"status":"pending"}]}""")]
    public void Anything_else_is_refused(string json)
    {
        Assert.False(AlertMessage.TryParse(json, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Too_many_alerts_are_refused()
    {
        var alerts = Enumerable.Range(0, AlertMessage.MaxAlerts + 1).Select(i => Setup.Firing(fingerprint: $"fp{i}")).ToArray();

        Assert.False(AlertMessage.TryParse(Setup.Message(alerts), out _, out _));
    }

    [Fact]
    public void The_key_is_the_alertmanager_fingerprint_when_it_is_plain_else_a_hash_of_the_labels()
    {
        var plain = Setup.Parse(Setup.Message(Setup.Firing(fingerprint: "ABC123"))).Alerts![0];
        var hostile = Setup.Parse(Setup.Message(Setup.Firing(fingerprint: "x --> <script>"))).Alerts![0];
        var same = Setup.Parse(Setup.Message(Setup.Firing(fingerprint: ""))).Alerts![0];

        Assert.Equal("abc123", plain.Key);
        Assert.Matches("^[0-9a-f]{16}$", hostile.Key); // it ends up in a hidden comment, so it can never carry markup
        Assert.Equal(hostile.Key, same.Key);
    }
}

public sealed class SettingsTests
{
    private static IntakeSettings Load(params (string Key, string? Value)[] values) =>
        IntakeSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build());

    private const string OneHost = """[{"name":"forgejo","url":"http://forgejo:3000","token":"t"}]""";

    [Fact]
    public void Defaults_are_safe_a_dry_run_and_a_small_limit()
    {
        var settings = Load(("ALERT_INTAKE_TOKEN", "0123456789abcdef"), ("ALERT_INTAKE_HOSTS", OneHost));

        Assert.True(settings.DryRun);
        Assert.Equal(6, settings.MaxTicketsPerHour);
        Assert.Equal([new IntakeHost("forgejo", "forgejo", "http://forgejo:3000", "t")], settings.Hosts);
    }

    [Theory]
    [InlineData("short", "ALERT_INTAKE_TOKEN")]
    public void A_missing_or_short_token_is_refused_without_echoing_it(string token, string name)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(("ALERT_INTAKE_TOKEN", token), ("ALERT_INTAKE_HOSTS", OneHost)));
        Assert.Contains(name, error.Message);
        Assert.DoesNotContain(token, error.Message.Replace(name, ""));
    }

    [Fact]
    public void Writing_tickets_needs_a_token_on_every_host_and_the_limits_are_bounded()
    {
        Assert.Contains("ALERT_INTAKE_HOSTS", Assert.Throws<InvalidOperationException>(() =>
            Load(("ALERT_INTAKE_TOKEN", "0123456789abcdef"))).Message);
        Assert.Contains("token", Assert.Throws<InvalidOperationException>(() =>
            Load(("ALERT_INTAKE_TOKEN", "0123456789abcdef"), ("ALERT_INTAKE_DRY_RUN", "false"), ("ALERT_INTAKE_HOSTS", """[{"name":"forgejo","url":"http://forgejo:3000"}]"""))).Message);
        Assert.Contains("PER_HOUR", Assert.Throws<InvalidOperationException>(() =>
            Load(("ALERT_INTAKE_TOKEN", "0123456789abcdef"), ("ALERT_INTAKE_HOSTS", OneHost), ("ALERT_INTAKE_MAX_TICKETS_PER_HOUR", "0"))).Message);
        Assert.Contains("http or https", Assert.Throws<InvalidOperationException>(() =>
            Load(("ALERT_INTAKE_TOKEN", "0123456789abcdef"), ("ALERT_INTAKE_HOSTS", """[{"name":"forgejo","url":"ftp://x"}]"""))).Message);
    }
}
