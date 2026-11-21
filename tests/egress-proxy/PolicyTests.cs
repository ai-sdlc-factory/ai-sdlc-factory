using System.Net;

namespace DevFoundry.EgressProxy.Tests;

public sealed class EgressConfigTests
{
    private static EgressConfig Parse(string json)
    {
        Assert.True(EgressConfigParser.TryParse(json, out var config, out var error), error);
        return config!;
    }

    private static string Problem(string? json)
    {
        Assert.False(EgressConfigParser.TryParse(json, out _, out var error));
        return error;
    }

    [Fact]
    public void Profiles_include_the_hosts_of_the_profiles_they_name()
    {
        var config = Parse("""{"ports":[443],"profiles":{"a":{"hosts":["a.example.com"]},"b":{"hosts":["B.Example.com"]},"c":{"include":["a","b"],"hosts":["c.example.com"]}}}""");

        Assert.Equal(["a.example.com"], config.Profiles["a"].Hosts);
        Assert.Equal(["c.example.com", "a.example.com", "b.example.com"], config.Profiles["c"].Hosts);
        Assert.False(config.Profiles["c"].Audit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"profiles":{}}""")]
    [InlineData("""{"ports":[],"profiles":{"a":{}}}""")]
    [InlineData("""{"ports":[0],"profiles":{"a":{}}}""")]
    [InlineData("""{"ports":[443]}""")]
    [InlineData("""{"ports":[443],"profiles":{}}""")]
    [InlineData("""{"ports":[443],"profiles":{"Bad Name":{}}}""")]
    public void Unusable_files_are_rejected_with_a_reason(string json) => Assert.NotEmpty(Problem(json));

    [Theory]
    [InlineData("localhost")]
    [InlineData("10.0.0.1")]
    [InlineData("1.2.3.4")]
    [InlineData("*")]
    [InlineData("*.com.")]
    [InlineData("exa*mple.com")]
    [InlineData("**.example.com")]
    [InlineData("example.com/path")]
    [InlineData("https://example.com")]
    [InlineData("exa mple.com")]
    [InlineData("-bad.example.com")]
    public void Only_real_host_names_and_leading_wildcards_are_accepted(string host) =>
        Assert.Contains("is not a host name", Problem("{\"ports\":[443],\"profiles\":{\"a\":{\"hosts\":[\"" + host + "\"]}}}"));

    [Fact]
    public void Audit_mode_for_the_whole_file_turns_every_profile_to_audit_and_keeps_the_hosts()
    {
        var enforced = Parse("""{"ports":[443],"profiles":{"a":{"hosts":["a.example.com"]},"b":{"hosts":["b.example.com"]}}}""");

        var audited = enforced.WithAuditMode();

        Assert.All(audited.Profiles.Values, profile => Assert.True(profile.Audit));
        Assert.Equal(enforced.Profiles["a"].Hosts, audited.Profiles["a"].Hosts);
        Assert.Equal(enforced.Ports, audited.Ports);
        Assert.All(enforced.Profiles.Values, profile => Assert.False(profile.Audit)); // the original is untouched
    }

    [Fact]
    public void Includes_must_exist_and_cannot_nest_or_loop()
    {
        Assert.Contains("includes", Problem("""{"ports":[443],"profiles":{"a":{"include":["missing"]}}}"""));
        Assert.Contains("includes", Problem("""{"ports":[443],"profiles":{"a":{"include":["a"]}}}"""));
        Assert.Contains("includes", Problem("""{"ports":[443],"profiles":{"a":{"include":["b"]},"b":{"include":["a"]}}}"""));
        Assert.Contains("includes", Problem("""{"ports":[443],"profiles":{"a":{},"b":{"include":["a"]},"c":{"include":["b"]}}}"""));
    }

    [Fact]
    public void Audit_mode_is_only_on_when_asked_for()
    {
        Assert.True(Parse("""{"ports":[443],"profiles":{"a":{"mode":"audit"}}}""").Profiles["a"].Audit);
        Assert.False(Parse("""{"ports":[443],"profiles":{"a":{"mode":"enforce"}}}""").Profiles["a"].Audit);
        Assert.Contains("mode", Problem("""{"ports":[443],"profiles":{"a":{"mode":"allow-all"}}}"""));
    }

    // ---- the allowlist that ships with the factory ----------------------------------------------------------

    private static EgressConfig Shipped()
    {
        Assert.True(EgressConfigParser.TryParse(File.ReadAllText("profiles.json"), out var config, out var error), error);
        return config!;
    }

    [Fact]
    public void The_shipped_allowlist_is_valid_and_has_the_profiles_the_launcher_uses()
    {
        var config = Shipped();
        Assert.Equal(["developer", "model", "packages"], config.Profiles.Keys.Order());
        Assert.Equal([443], config.Ports);
    }

    [Fact]
    public void The_shipped_profiles_never_run_in_audit_mode()
    {
        Assert.All(Shipped().Profiles.Values, profile => Assert.False(profile.Audit, profile.Name));
    }

    [Fact]
    public void The_model_profile_reaches_the_model_provider_and_nothing_that_installs_code()
    {
        var model = Shipped().Profiles["model"];

        Assert.Contains("chatgpt.com", model.Hosts);
        Assert.Contains("auth.openai.com", model.Hosts);
        Assert.DoesNotContain(model.Hosts, host => host.Contains("npm") || host.Contains("pypi") || host.Contains("nuget") || host.StartsWith("*"));
        Assert.True(model.Hosts.Count <= 4, "the model profile should stay small");
    }

    [Fact]
    public void The_developer_profile_is_exactly_the_model_and_package_hosts()
    {
        var config = Shipped();
        var expected = config.Profiles["model"].Hosts.Concat(config.Profiles["packages"].Hosts).Order();
        Assert.Equal(expected, config.Profiles["developer"].Hosts.Order());
    }

    [Fact]
    public void No_shipped_profile_allows_a_broad_wildcard_or_a_general_purpose_host()
    {
        var broad = new[] { "github.com", "raw.githubusercontent.com", "gist.github.com", "pastebin.com", "transfer.sh", "amazonaws.com", "*.amazonaws.com" };
        foreach (var profile in Shipped().Profiles.Values)
        {
            Assert.DoesNotContain(profile.Hosts, host => broad.Contains(host));
            Assert.DoesNotContain(profile.Hosts, host => host.StartsWith("*.") && host.Count(c => c == '.') < 2);
        }
    }
}

public sealed class EgressPolicyTests
{
    private static EgressPolicy Policy(params (string Name, string[] Hosts, bool Audit)[] profiles) =>
        new(new EgressConfig(
                new HashSet<int> { 443 },
                profiles.ToDictionary(p => p.Name, p => new EgressProfile(p.Name, p.Hosts, p.Audit))),
            new Dictionary<IPAddress, string> { [IPAddress.Parse("10.250.1.2")] = "model" });

    private static EgressPolicy Model(params string[] hosts) => Policy(("model", hosts, false));

    [Fact]
    public void A_listed_host_on_an_allowed_port_is_allowed()
    {
        var decision = Model("chatgpt.com").Decide("model", "chatgpt.com", 443);
        Assert.True(decision.Allowed);
        Assert.False(decision.Audit);
    }

    [Theory]
    [InlineData("CHATGPT.com", true)]
    [InlineData("chatgpt.com.", true)]
    [InlineData("evilchatgpt.com", false)]
    [InlineData("chatgpt.com.evil.net", false)]
    [InlineData("sub.chatgpt.com", false)]
    [InlineData("example.com", false)]
    public void Exact_entries_match_only_that_name(string host, bool allowed) =>
        Assert.Equal(allowed, Model("chatgpt.com").Decide("model", host, 443).Allowed);

    [Theory]
    [InlineData("a.example.com", true)]
    [InlineData("deep.a.example.com", true)]
    [InlineData("example.com", false)]
    [InlineData("notexample.com", false)]
    [InlineData("a.example.com.evil.net", false)]
    [InlineData("xexample.com", false)]
    public void A_wildcard_matches_subdomains_but_not_the_bare_domain(string host, bool allowed) =>
        Assert.Equal(allowed, Model("*.example.com").Decide("model", host, 443).Allowed);

    [Theory]
    [InlineData(80)]
    [InlineData(22)]
    [InlineData(8443)]
    public void Other_ports_are_refused_even_for_listed_hosts(int port)
    {
        var decision = Model("chatgpt.com").Decide("model", "chatgpt.com", port);
        Assert.False(decision.Allowed);
        Assert.Contains("port", decision.Reason);
    }

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void IP_addresses_are_refused_by_name_rules(string host)
    {
        var decision = Model("chatgpt.com").Decide("model", host, 443);
        Assert.False(decision.Allowed);
        Assert.Contains("IP address", decision.Reason);
    }

    [Fact]
    public void Whole_file_audit_mode_still_refuses_what_audit_never_lifts()
    {
        var audited = new EgressPolicy(
            new EgressConfig(new HashSet<int> { 443 }, new Dictionary<string, EgressProfile> { ["model"] = new("model", ["chatgpt.com"], false) }).WithAuditMode(),
            new Dictionary<IPAddress, string>());

        Assert.True(audited.Decide("model", "anything.example.org", 443).Audit);
        Assert.False(audited.Decide("model", "anything.example.org", 25).Allowed);
        Assert.False(audited.Decide("model", "8.8.8.8", 443).Allowed);
    }

    [Fact]
    public void An_unknown_profile_gets_nothing() =>
        Assert.False(Model("chatgpt.com").Decide("ghost", "chatgpt.com", 443).Allowed);

    [Fact]
    public void Audit_mode_allows_and_flags_unlisted_hosts_but_keeps_the_other_rules()
    {
        var policy = Policy(("probe", ["listed.example.com"], true));

        var unlisted = policy.Decide("probe", "other.example.net", 443);
        Assert.True(unlisted.Allowed);
        Assert.True(unlisted.Audit);
        Assert.False(policy.Decide("probe", "listed.example.com", 443).Audit);
        Assert.False(policy.Decide("probe", "other.example.net", 80).Allowed);
        Assert.False(policy.Decide("probe", "1.2.3.4", 443).Allowed);
    }

    [Fact]
    public void The_profile_follows_the_address_the_sandbox_connected_to()
    {
        var policy = Model("chatgpt.com");

        Assert.Equal("model", policy.ProfileFor(IPAddress.Parse("10.250.1.2")));
        Assert.Equal("model", policy.ProfileFor(IPAddress.Parse("10.250.1.2").MapToIPv6()));
        Assert.Null(policy.ProfileFor(IPAddress.Parse("10.250.2.2")));
        Assert.Null(policy.ProfileFor(IPAddress.Loopback));
    }

    [Fact]
    public void The_listen_map_is_parsed_and_validated()
    {
        Assert.True(EgressPolicy.TryParseListenMap("model=10.250.1.2, developer=10.250.3.2", out var map, out _));
        Assert.Equal("developer", map[IPAddress.Parse("10.250.3.2")]);

        Assert.False(EgressPolicy.TryParseListenMap("", out _, out _));
        Assert.False(EgressPolicy.TryParseListenMap("model", out _, out _));
        Assert.False(EgressPolicy.TryParseListenMap("model=not-an-ip", out _, out _));
        Assert.False(EgressPolicy.TryParseListenMap("a=10.0.0.2,b=10.0.0.2", out _, out var error));
        Assert.Contains("twice", error);
    }
}

public sealed class AddressRulesTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("104.18.32.47")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string address) => Assert.True(AddressRules.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.250.3.7")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.65.254")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void Private_loopback_metadata_and_reserved_addresses_are_refused(string address) =>
        Assert.False(AddressRules.IsPublic(IPAddress.Parse(address)));
}

public sealed class ConnectRequestParserTests
{
    private static ParseFailure Parse(string head, out ConnectRequest? request) =>
        ConnectRequestParser.TryParse(System.Text.Encoding.ASCII.GetBytes(head), out request);

    [Fact]
    public void A_connect_request_gives_host_and_port()
    {
        Assert.Equal(ParseFailure.None, Parse("CONNECT chatgpt.com:443 HTTP/1.1\r\nHost: chatgpt.com:443\r\nProxy-Connection: keep-alive", out var request));
        Assert.Equal(new ConnectRequest("chatgpt.com", 443), request);
        Assert.Equal(ParseFailure.None, Parse("CONNECT a.b:8443 HTTP/1.0", out request));
        Assert.Equal(8443, request!.Port);
    }

    [Theory]
    [InlineData("GET http://example.com/ HTTP/1.1")]
    [InlineData("POST http://example.com/ HTTP/1.1")]
    [InlineData("connect a.com:443 HTTP/1.1")]
    public void Other_methods_are_not_supported(string head) =>
        Assert.Equal(ParseFailure.NotConnect, Parse(head, out _));

    [Theory]
    [InlineData("GET http://Registry.Example.com/pkg HTTP/1.1", "registry.example.com")]
    [InlineData("GET http://127.0.0.1:5080/api/cities HTTP/1.1", "127.0.0.1")]
    [InlineData("GET /relative HTTP/1.1", "-")]
    [InlineData("GET http://bad\u0001host/ HTTP/1.1", "-")]
    public void A_refused_plain_request_is_logged_with_its_host(string head, string expected) =>
        Assert.Equal(expected, ConnectRequestParser.PlainHttpHost(System.Text.Encoding.ASCII.GetBytes(head + "\r\n\r\n")));

    [Theory]
    [InlineData("")]
    [InlineData("CONNECT")]
    [InlineData("CONNECT a.com:443")]
    [InlineData("CONNECT a.com:443 HTTP/2")]
    [InlineData("CONNECT a.com HTTP/1.1")]
    [InlineData("CONNECT a.com:0 HTTP/1.1")]
    [InlineData("CONNECT a.com:70000 HTTP/1.1")]
    [InlineData("CONNECT a.com:abc HTTP/1.1")]
    [InlineData("CONNECT :443 HTTP/1.1")]
    [InlineData("CONNECT [::1]:443 HTTP/1.1")]
    [InlineData("CONNECT a_b.com:443 HTTP/1.1")]
    [InlineData("CONNECT a.com/x:443 HTTP/1.1")]
    [InlineData("CONNECT a.com:443 HTTP/1.1 extra")]
    public void Malformed_requests_are_refused(string head) =>
        Assert.Equal(ParseFailure.Malformed, Parse(head, out _));

    [Fact]
    public void Control_characters_never_reach_the_host()
    {
        Assert.Equal(ParseFailure.Malformed, Parse("CONNECT a.com\u0001b:443 HTTP/1.1", out _));
        Assert.Equal(ParseFailure.Malformed, Parse("CONNECT a.com\"}:443 HTTP/1.1", out _));
    }
}
