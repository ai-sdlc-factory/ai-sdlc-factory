using System.Text.Json;

namespace DevFoundry.SandboxLauncher.Tests;

public sealed class ContainerSpecTests
{
    private static JsonElement Spec(SandboxRunRequest? request = null, string? globalConfig = null)
    {
        var settings = Support.Settings(globalConfig);
        var body = ContainerSpec.Build(Support.Valid(request, settings), settings);
        return JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement;
    }

    [Fact]
    public void Container_is_locked_down()
    {
        var host = Spec().GetProperty("HostConfig");

        Assert.True(host.GetProperty("ReadonlyRootfs").GetBoolean());
        Assert.False(host.GetProperty("Privileged").GetBoolean());
        Assert.Equal(["ALL"], host.GetProperty("CapDrop").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["no-new-privileges:true"], host.GetProperty("SecurityOpt").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(256, host.GetProperty("PidsLimit").GetInt32());
        Assert.Equal(host.GetProperty("Memory").GetInt64(), host.GetProperty("MemorySwap").GetInt64());
        Assert.Equal("dev-foundry_sandbox", host.GetProperty("NetworkMode").GetString());
    }

    [Fact]
    public void Container_never_gets_devices_binds_or_host_namespaces()
    {
        var host = Spec().GetProperty("HostConfig");
        foreach (var forbidden in new[] { "Binds", "Devices", "PidMode", "IpcMode", "UsernsMode", "CapAdd", "Sysctls" })
        {
            Assert.False(host.TryGetProperty(forbidden, out _), forbidden);
        }
    }

    [Fact]
    public void Runs_as_the_configured_unprivileged_user()
    {
        Assert.Equal("1000:1000", Spec().GetProperty("User").GetString());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Workspace_is_the_only_mount_and_follows_the_writable_flag(bool writable, bool readOnly)
    {
        var mounts = Spec(Support.Request(writable: writable)).GetProperty("HostConfig").GetProperty("Mounts").EnumerateArray().ToList();

        var mount = Assert.Single(mounts);
        Assert.Equal("bind", mount.GetProperty("Type").GetString());
        Assert.Equal("/work/feature-acme-shop-1", mount.GetProperty("Source").GetString());
        Assert.Equal("/workspace", mount.GetProperty("Target").GetString());
        Assert.Equal(readOnly, mount.GetProperty("ReadOnly").GetBoolean());
    }

    [Fact]
    public void Global_settings_are_mounted_read_only_when_configured()
    {
        var mounts = Spec(globalConfig: "/srv/factory-config").GetProperty("HostConfig").GetProperty("Mounts").EnumerateArray().ToList();

        Assert.Equal(2, mounts.Count);
        Assert.Equal("/srv/factory-config", mounts[1].GetProperty("Source").GetString());
        Assert.Equal("/etc/factory", mounts[1].GetProperty("Target").GetString());
        Assert.True(mounts[1].GetProperty("ReadOnly").GetBoolean());
    }

    [Fact]
    public void Container_is_labelled_so_leftovers_can_be_found()
    {
        Assert.Equal("true", Spec().GetProperty("Labels").GetProperty("dev-foundry.sandbox").GetString());
    }
}
