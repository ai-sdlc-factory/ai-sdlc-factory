namespace DevFoundry.SandboxLauncher.Tests;

internal static class Support
{
    public static SandboxSettings Settings(
        string? globalConfig = null, string? authVolume = "dev-foundry_opencode-data", bool authReadOnly = false) => new()
    {
        WorkspaceRoot = "/work",
        Images = new Dictionary<string, string> { ["dotnet"] = "dev-foundry/sandbox-dotnet:local" },
        GlobalConfigPath = globalConfig,
        AuthVolume = authVolume,
        AuthReadOnly = authReadOnly,
    };

    public static SandboxRunRequest Request(
        string image = "dotnet", string workspace = "feature-acme-shop-1", bool writable = false,
        string[]? command = null, int timeout = 60, Dictionary<string, string>? env = null,
        bool modelAuth = false) =>
        new(image, workspace, writable, command ?? ["git", "status"], timeout, env, modelAuth);

    public static ValidatedRun Valid(SandboxRunRequest? request = null, SandboxSettings? settings = null) =>
        SandboxPolicy.Validate(request ?? Request(), settings ?? Settings(), _ => true);
}
