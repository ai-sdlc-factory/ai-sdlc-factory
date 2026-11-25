namespace AiSdlcFactory.SandboxLauncher.Tests;

internal static class Support
{
    public static SandboxSettings Settings(
        string? globalConfig = null, string? authVolume = "ai-sdlc-factory_opencode-data", bool authReadOnly = false,
        EgressMode egress = EgressMode.Enforce, bool allowRepoImages = false, string? model = null, Dictionary<string, string>? roleModels = null) => new()
    {
        WorkspaceRoot = "/work",
        Images = new Dictionary<string, string> { ["dotnet"] = "ai-sdlc-factory/sandbox-dotnet:local" },
        Networks = new Dictionary<string, string>
        {
            ["model"] = "ai-sdlc-factory_sandbox-model",
            ["packages"] = "ai-sdlc-factory_sandbox-packages",
            ["developer"] = "ai-sdlc-factory_sandbox-developer",
        },
        Proxies = new Dictionary<string, string>
        {
            ["model"] = "http://10.250.1.2:3128",
            ["packages"] = "http://10.250.2.2:3128",
            ["developer"] = "http://10.250.3.2:3128",
        },
        GlobalConfigPath = globalConfig,
        AuthVolume = authVolume,
        AuthReadOnly = authReadOnly,
        Egress = egress,
        AllowRepoImages = allowRepoImages,
        Model = model,
        RoleModels = roleModels ?? new Dictionary<string, string>(),
    };

    public static SandboxRunRequest Request(
        string image = "dotnet", string workspace = "feature-acme-shop-1", bool writable = false,
        string[]? command = null, int timeout = 60, Dictionary<string, string>? env = null,
        bool modelAuth = false, string network = SandboxNetworks.None, string? role = null) =>
        new(image, workspace, writable, command ?? ["git", "status"], timeout, env, modelAuth, network, Role: role);

    public static ValidatedRun Valid(SandboxRunRequest? request = null, SandboxSettings? settings = null) =>
        SandboxPolicy.Validate(request ?? Request(), settings ?? Settings(), _ => true);
}
