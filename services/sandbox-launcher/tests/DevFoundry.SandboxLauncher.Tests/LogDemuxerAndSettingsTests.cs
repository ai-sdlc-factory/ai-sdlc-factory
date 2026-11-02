using System.Text;

namespace DevFoundry.SandboxLauncher.Tests;

public sealed class LogDemuxerAndSettingsTests
{
    private static byte[] Frame(byte stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[8 + payload.Length];
        frame[0] = stream;
        frame[4] = (byte)(payload.Length >> 24);
        frame[5] = (byte)(payload.Length >> 16);
        frame[6] = (byte)(payload.Length >> 8);
        frame[7] = (byte)payload.Length;
        payload.CopyTo(frame, 8);
        return frame;
    }

    private static MemoryStream Stream(params byte[][] frames) => new(frames.SelectMany(f => f).ToArray());

    [Fact]
    public async Task Frames_are_split_into_stdout_and_stderr()
    {
        var logs = await LogDemuxer.ReadAsync(
            Stream(Frame(1, "hello "), Frame(2, "oops"), Frame(1, "world")), 1000, CancellationToken.None);

        Assert.Equal("hello world", logs.Stdout);
        Assert.Equal("oops", logs.Stderr);
        Assert.False(logs.Truncated);
    }

    [Fact]
    public async Task Output_beyond_the_limit_is_dropped_and_flagged()
    {
        var logs = await LogDemuxer.ReadAsync(
            Stream(Frame(1, new string('a', 100)), Frame(2, "b")), 10, CancellationToken.None);

        Assert.Equal(new string('a', 10), logs.Stdout);
        Assert.Equal("b", logs.Stderr);
        Assert.True(logs.Truncated);
    }

    [Fact]
    public async Task Empty_stream_gives_empty_output()
    {
        var logs = await LogDemuxer.ReadAsync(new MemoryStream(), 10, CancellationToken.None);
        Assert.Equal(new DockerLogs("", "", false), logs);
    }

    [Fact]
    public void Image_list_is_parsed()
    {
        var images = SandboxSettings.ParseImages("dotnet=a/b:1; python = c/d:2 ;");
        Assert.Equal("a/b:1", images["dotnet"]);
        Assert.Equal("c/d:2", images["python"]);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("=image")]
    [InlineData("alias=")]
    public void Malformed_image_list_is_rejected(string value) =>
        Assert.Throws<InvalidOperationException>(() => SandboxSettings.ParseImages(value));

    [Theory]
    [InlineData("relative/path")]
    [InlineData("")]
    public void Workspace_root_must_be_absolute(string root) =>
        Assert.Throws<InvalidOperationException>(() =>
            new SandboxSettings { WorkspaceRoot = root, Images = Support.Settings().Images }.Validate());

    [Fact]
    public void Sandbox_cannot_run_as_root() =>
        Assert.Throws<InvalidOperationException>(() =>
            new SandboxSettings { WorkspaceRoot = "/work", Images = Support.Settings().Images, UserId = 0 }.Validate());
}
