namespace DevFoundry.SandboxLauncher.Tests;

internal static class Support
{
    public static SandboxSettings Settings(string? globalConfig = null) => new()
    {
        WorkspaceRoot = "/work",
        Images = new Dictionary<string, string> { ["dotnet"] = "dev-foundry/sandbox-dotnet:local" },
        GlobalConfigPath = globalConfig,
    };

    public static SandboxRunRequest Request(
        string image = "dotnet", string workspace = "feature-acme-shop-1", bool writable = false,
        string[]? command = null, int timeout = 60, Dictionary<string, string>? env = null) =>
        new(image, workspace, writable, command ?? ["git", "status"], timeout, env);

    public static ValidatedRun Valid(SandboxRunRequest? request = null, SandboxSettings? settings = null) =>
        SandboxPolicy.Validate(request ?? Request(), settings ?? Settings(), _ => true);
}
