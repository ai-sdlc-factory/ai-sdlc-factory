using System.Text.Json;
using Temporalio.Exceptions;

namespace DevFoundry.SandboxLauncher.Tests;

public sealed class EgressSpecTests
{
    private static JsonElement Spec(SandboxRunRequest request) =>
        JsonDocument.Parse(JsonSerializer.Serialize(ContainerSpec.Build(Support.Valid(request), Support.Settings()))).RootElement;

    private static string[] Env(JsonElement spec) => spec.GetProperty("Env").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string NetworkMode(JsonElement spec) => spec.GetProperty("HostConfig").GetProperty("NetworkMode").GetString()!;

    [Fact]
    public void A_run_without_a_network_profile_has_no_network_and_no_proxy_settings()
    {
        var spec = Spec(Support.Request());

        Assert.Equal("none", NetworkMode(spec));
        Assert.DoesNotContain(Env(spec), e => e.Contains("PROXY", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("model", "dev-foundry_sandbox-model", "http://10.250.1.2:3128")]
    [InlineData("packages", "dev-foundry_sandbox-packages", "http://10.250.2.2:3128")]
    [InlineData("developer", "dev-foundry_sandbox-developer", "http://10.250.3.2:3128")]
    public void A_profile_puts_the_run_on_its_own_internal_network_with_its_own_proxy(string profile, string network, string proxy)
    {
        var spec = Spec(Support.Request(network: profile));
        var env = Env(spec);

        Assert.Equal(network, NetworkMode(spec));
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
        {
            Assert.Contains($"{name}={proxy}", env);
        }
        Assert.Contains("NO_PROXY=localhost,127.0.0.1", env);
    }

    [Theory]
    [InlineData("model", true)]
    [InlineData("developer", false)]
    [InlineData("packages", false)]
    [InlineData("none", false)]
    public void Only_read_only_agents_have_the_npm_registry_pointed_at_a_dead_address(string network, bool dead) =>
        Assert.Equal(dead, Env(Spec(Support.Request(network: network))).Contains("NPM_CONFIG_REGISTRY=http://127.0.0.1:9"));

    [Fact]
    public void A_run_cannot_see_the_proxy_of_another_profile()
    {
        var env = Env(Spec(Support.Request(network: "model")));

        Assert.DoesNotContain(env, e => e.Contains("10.250.3.2") || e.Contains("10.250.2.2"));
    }

    [Fact]
    public void OpenCodes_downloads_and_update_checks_are_always_switched_off()
    {
        foreach (var network in new[] { "none", "model", "developer" })
        {
            var env = Env(Spec(Support.Request(network: network)));
            Assert.Contains("OPENCODE_DISABLE_MODELS_FETCH=1", env);
            Assert.Contains("OPENCODE_DISABLE_AUTOUPDATE=1", env);
            Assert.Contains("OPENCODE_DISABLE_LSP_DOWNLOAD=1", env);
        }
    }

    [Fact]
    public void A_request_cannot_override_the_fixed_environment_it_comes_before_the_fixed_values()
    {
        var env = Env(Spec(Support.Request(network: "model", env: new() { ["OPENCODE_DISABLE_MODELS_FETCH"] = "0" })));

        // Docker uses the last value of a repeated variable, and the launcher's values come last.
        Assert.Equal("OPENCODE_DISABLE_MODELS_FETCH=1", env.Last(e => e.StartsWith("OPENCODE_DISABLE_MODELS_FETCH=")));
    }

    [Fact]
    public void A_request_cannot_set_proxy_variables_at_all()
    {
        Assert.Throws<ApplicationFailureException>(() => Support.Valid(Support.Request(env: new() { ["HTTPS_PROXY"] = "http://evil:3128" })));
        Assert.Throws<ApplicationFailureException>(() => Support.Valid(Support.Request(env: new() { ["NO_PROXY"] = "*" })));
    }
}

public sealed class EgressPolicyTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("model")]
    [InlineData("packages")]
    [InlineData("developer")]
    public void Known_profiles_and_none_are_accepted(string network) =>
        Assert.Equal(network, Support.Valid(Support.Request(network: network)).Network);

    [Theory]
    [InlineData("internet")]
    [InlineData("DEVELOPER")]
    [InlineData("")]
    [InlineData("dev-foundry_sandbox-developer")]
    [InlineData("host")]
    [InlineData("bridge")]
    public void Anything_else_is_rejected_without_a_retry(string network)
    {
        var error = Assert.Throws<ApplicationFailureException>(() => Support.Valid(Support.Request(network: network)));
        Assert.True(error.NonRetryable);
        Assert.Contains("Unknown network profile", error.Message);
    }

    [Fact]
    public void A_request_that_leaves_the_network_out_gets_none()
    {
        // An older caller that does not know about networks must end up with the safest choice.
        var json = """{"image":"dotnet","workspace":"feature-x-1","workspace_writable":false,"command":["git","status"],"timeout_seconds":60}""";
        var request = JsonSerializer.Deserialize<SandboxRunRequest>(json)!;

        Assert.Equal("none", request.Network);
        Assert.Equal("none", SandboxPolicy.Validate(request, Support.Settings(), _ => true).Network);
    }
}

public sealed class EgressSettingsTests
{
    private static SandboxSettings Build(string networks, string proxies) => new()
    {
        WorkspaceRoot = "/work",
        Images = Support.Settings().Images,
        Networks = SandboxSettings.ParsePairs(networks, "SANDBOX_NETWORKS"),
        Proxies = SandboxSettings.ParsePairs(proxies, "SANDBOX_PROXIES"),
    };

    [Fact]
    public void Matching_networks_and_proxies_are_accepted() =>
        Build("a=net-a;b=net-b", "a=http://10.0.1.2:3128;b=http://10.0.2.2:3128").Validate();

    [Theory]
    [InlineData("", "")]
    [InlineData("a=net-a", "")]
    [InlineData("a=net-a", "b=http://10.0.1.2:3128")]
    [InlineData("a=net-a;b=net-b", "a=http://10.0.1.2:3128")]
    public void Profiles_need_both_a_network_and_a_proxy(string networks, string proxies) =>
        Assert.Throws<InvalidOperationException>(() => Build(networks, proxies).Validate());

    [Theory]
    [InlineData("http://proxy:3128")]
    [InlineData("https://10.0.1.2:3128")]
    [InlineData("10.0.1.2:3128")]
    [InlineData("http://10.0.1.2")]
    [InlineData("http://10.0.1.2:3128/path")]
    public void Proxy_addresses_must_be_plain_http_ip_and_port(string proxy) =>
        Assert.Throws<InvalidOperationException>(() => Build("a=net-a", $"a={proxy}").Validate());

    [Fact]
    public void None_is_reserved() =>
        Assert.Throws<InvalidOperationException>(() => Build("none=net-a", "none=http://10.0.1.2:3128").Validate());
}

public sealed class StartupCheckTests
{
    private sealed class Docker(HashSet<string> networks) : IDockerClient
    {
        public List<string> Removed { get; } = [];
        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<DockerImage?> InspectImageAsync(string name, CancellationToken ct) => Task.FromResult<DockerImage?>(null);
        public Task BuildImageAsync(Stream context, string tag, DockerBuildLimits limits, IReadOnlyDictionary<string, string> labels, Action<string> onLine, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public List<string> Created { get; } = [];
        public Task<bool> NetworkExistsAsync(string name, CancellationToken ct) => Task.FromResult(networks.Contains(name));
        public Task EnsureNetworkAsync(string name, CancellationToken ct) { Created.Add(name); return Task.CompletedTask; }
        public Task<bool> VolumeExistsAsync(string name, CancellationToken ct) => Task.FromResult(true);
        public Task<string> CreateAsync(CreateContainerBody body, CancellationToken ct) => throw new NotSupportedException();
        public Task StartAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> WaitAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task KillAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<DockerLogs> LogsAsync(string id, int maxBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken ct) { Removed.Add(id); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> ListSandboxIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["leftover-container-id"]);
    }

    private static SandboxJanitor Janitor(Docker docker, EgressMode mode = EgressMode.Enforce) =>
        new(docker, Support.Settings(egress: mode), Microsoft.Extensions.Logging.Abstractions.NullLogger<SandboxJanitor>.Instance);

    [Fact]
    public async Task In_open_mode_the_launcher_creates_the_open_network_and_does_not_need_the_egress_networks()
    {
        var docker = new Docker([]);

        await Janitor(docker, EgressMode.Open).StartAsync(CancellationToken.None);

        Assert.Equal(["dev-foundry_sandbox-open"], docker.Created);
        Assert.Equal(["leftover-container-id"], docker.Removed);
    }

    [Fact]
    public async Task In_enforce_mode_it_never_creates_an_open_network()
    {
        var docker = new Docker(["dev-foundry_sandbox-model", "dev-foundry_sandbox-packages", "dev-foundry_sandbox-developer"]);

        await Janitor(docker).StartAsync(CancellationToken.None);

        Assert.Empty(docker.Created);
    }

    [Fact]
    public async Task The_launcher_refuses_to_start_when_an_egress_network_is_missing()
    {
        var docker = new Docker(["dev-foundry_sandbox-model", "dev-foundry_sandbox-packages"]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Janitor(docker).StartAsync(CancellationToken.None));

        Assert.Contains("dev-foundry_sandbox-developer", error.Message);
        Assert.Contains("egress-proxy", error.Message);
    }

    [Fact]
    public async Task With_every_network_present_it_starts_and_removes_leftover_sandboxes()
    {
        var docker = new Docker(["dev-foundry_sandbox-model", "dev-foundry_sandbox-packages", "dev-foundry_sandbox-developer"]);

        await Janitor(docker).StartAsync(CancellationToken.None);

        Assert.Equal(["leftover-container-id"], docker.Removed);
    }
}

public sealed class OpenModeTests
{
    private static JsonElement Spec(SandboxRunRequest request, EgressMode mode)
    {
        var settings = Support.Settings(egress: mode);
        return JsonDocument.Parse(JsonSerializer.Serialize(ContainerSpec.Build(Support.Valid(request, settings), settings))).RootElement;
    }

    private static string[] Env(JsonElement spec) => spec.GetProperty("Env").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string NetworkMode(JsonElement spec) => spec.GetProperty("HostConfig").GetProperty("NetworkMode").GetString()!;

    [Theory]
    [InlineData("model")]
    [InlineData("packages")]
    [InlineData("developer")]
    public void In_open_mode_every_profile_gets_the_ordinary_network_and_no_proxy(string profile)
    {
        var spec = Spec(Support.Request(network: profile), EgressMode.Open);

        Assert.Equal("dev-foundry_sandbox-open", NetworkMode(spec));
        Assert.DoesNotContain(Env(spec), e => e.Contains("PROXY", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Env(spec), e => e.StartsWith("NPM_CONFIG_REGISTRY"));
    }

    [Fact]
    public void A_run_that_asks_for_no_network_still_gets_none_in_open_mode()
    {
        var spec = Spec(Support.Request(), EgressMode.Open);

        Assert.Equal("none", NetworkMode(spec));
    }

    [Fact]
    public void Open_mode_does_not_loosen_anything_else()
    {
        var host = Spec(Support.Request(network: "developer"), EgressMode.Open).GetProperty("HostConfig");

        Assert.True(host.GetProperty("ReadonlyRootfs").GetBoolean());
        Assert.False(host.GetProperty("Privileged").GetBoolean());
        Assert.Equal(["ALL"], host.GetProperty("CapDrop").EnumerateArray().Select(e => e.GetString()));
        Assert.Single(host.GetProperty("Mounts").EnumerateArray());
        Assert.Contains("OPENCODE_DISABLE_AUTOUPDATE=1", Env(Spec(Support.Request(network: "developer"), EgressMode.Open)));
    }

    [Theory]
    [InlineData(null, EgressMode.Enforce)]
    [InlineData("", EgressMode.Enforce)]
    [InlineData("enforce", EgressMode.Enforce)]
    [InlineData("ENFORCE", EgressMode.Enforce)]
    [InlineData("audit", EgressMode.Enforce)]
    [InlineData("open", EgressMode.Open)]
    [InlineData(" Open ", EgressMode.Open)]
    public void The_mode_setting_is_read_case_insensitively(string? value, EgressMode expected) =>
        Assert.Equal(expected, SandboxSettings.ParseEgress(value));

    [Theory]
    [InlineData("off")]
    [InlineData("disabled")]
    [InlineData("opn")]
    [InlineData("true")]
    public void A_mistyped_mode_stops_the_launcher_instead_of_guessing(string value) =>
        Assert.Throws<InvalidOperationException>(() => SandboxSettings.ParseEgress(value));
}
