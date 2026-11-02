using System.Runtime.Versioning;

namespace DevFoundry.OpenCodeWorker.Tests;

/// <summary>A temporary data directory plus a shell script standing in for the opencode binary.</summary>
[SupportedOSPlatform("linux")]
public sealed class FakeOpenCode : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("fake-opencode-");

    public FakeOpenCode(string script, string authJson = "{}", int timeoutSeconds = 240)
    {
        var data = Path.Combine(root.FullName, "data");
        Directory.CreateDirectory(Path.Combine(data, "opencode"));
        File.WriteAllText(Path.Combine(data, "opencode", "auth.json"), authJson);

        Executable = Path.Combine(root.FullName, "fake-opencode");
        File.WriteAllText(Executable, "#!/bin/sh\n" + script);
        File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Settings = new Settings { OpenCodeDataHome = data, OpenCodeTimeoutSeconds = timeoutSeconds };
    }

    public string Executable { get; }

    public Settings Settings { get; }

    public void Dispose() => root.Delete(recursive: true);
}
