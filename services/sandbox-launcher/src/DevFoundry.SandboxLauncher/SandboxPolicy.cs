using System.Text.RegularExpressions;
using Temporalio.Exceptions;

namespace DevFoundry.SandboxLauncher;

/// <summary>A request that passed the policy, resolved to concrete values.</summary>
public sealed record ValidatedRun(
    string Image,
    string WorkspacePath,
    bool WorkspaceWritable,
    IReadOnlyList<string> Command,
    TimeSpan Timeout,
    IReadOnlyList<string> Env,
    bool NeedsModelAuth,
    string Network,
    string? Role = null,
    string? Workspace = null);

/// <summary>
/// Everything a requester is allowed to influence, and nothing else. The caller is the trusted
/// controller, but the policy still treats the request as untrusted: the launcher holds Docker
/// access, so it is the last line of defence.
/// </summary>
public static partial class SandboxPolicy
{
    public const int MaxArguments = 64;
    public const int MaxArgumentLength = 48 * 1024;
    private static readonly string[] AllowedEnvPrefixes = ["OPENCODE_", "FACTORY_"];

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9._-]{0,127}\z")]
    private static partial Regex WorkspaceName();

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,31}\z")]
    private static partial Regex RoleName();

    [GeneratedRegex(@"\A[A-Z][A-Z0-9_]{0,63}\z")]
    private static partial Regex EnvName();

    /// <param name="repoImage">Resolves an alias of an image built from a repository (<c>repo-&lt;hash&gt;</c>) to its Docker tag, or null if
    /// repository images are off or no such image exists. The standard aliases come from the settings.</param>
    public static ValidatedRun Validate(
        SandboxRunRequest request, SandboxSettings settings, Func<string, bool> workspaceExists, Func<string, string?>? repoImage = null)
    {
        if (!settings.Images.TryGetValue(request.Image ?? "", out var image))
        {
            image = RepoImageNames.IsAlias(request.Image) ? repoImage?.Invoke(request.Image!) : null;
            if (image is null)
            {
                throw Reject($"Unknown sandbox image alias '{request.Image}'.");
            }
        }

        if (request.Workspace is null || !WorkspaceName().IsMatch(request.Workspace) || request.Workspace.Contains(".."))
        {
            throw Reject("Workspace must be a plain directory name.");
        }
        var path = $"{settings.WorkspaceRoot}/{request.Workspace}";
        if (!workspaceExists(path))
        {
            throw Reject($"Workspace '{request.Workspace}' does not exist.");
        }

        if (request.Command is null || request.Command.Count == 0 || request.Command.Count > MaxArguments
            || request.Command.Any(argument => argument is null || argument.Length > MaxArgumentLength || argument.Contains('\0')))
        {
            throw Reject("Command must be 1 to 64 arguments without NUL characters.");
        }

        var env = new List<string>();
        foreach (var (name, value) in request.Env ?? new Dictionary<string, string>())
        {
            if (!EnvName().IsMatch(name) || !AllowedEnvPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                throw Reject($"Environment variable '{name}' is not allowed.");
            }
            if (value is null || value.Length > MaxArgumentLength || value.Contains('\0'))
            {
                throw Reject($"Environment variable '{name}' has an invalid value.");
            }
            env.Add($"{name}={value}");
        }

        if (request.TimeoutSeconds < 1)
        {
            throw Reject("Timeout must be at least one second.");
        }
        var timeout = TimeSpan.FromSeconds(Math.Min(request.TimeoutSeconds, settings.MaxRunSeconds));

        if (request.NeedsModelAuth && settings.AuthVolume is null)
        {
            throw Reject("Model access is not configured (SANDBOX_AUTH_VOLUME is empty).");
        }

        var network = request.Network ?? SandboxNetworks.None;
        if (network != SandboxNetworks.None && !settings.Networks.ContainsKey(network))
        {
            throw Reject($"Unknown network profile '{network}'. Known: {SandboxNetworks.None}, {string.Join(", ", settings.Networks.Keys.Order())}.");
        }

        if (request.Role is not null && !RoleName().IsMatch(request.Role))
        {
            throw Reject("Role must be 1 to 32 lowercase letters, digits, '-' or '_'.");
        }

        return new ValidatedRun(image, path, request.WorkspaceWritable, request.Command, timeout, env, request.NeedsModelAuth, network,
            request.Role, request.Workspace);
    }

    // Policy violations will not fix themselves, so they are never retried.
    private static ApplicationFailureException Reject(string message) =>
        new($"Sandbox request rejected: {message}", errorType: "SandboxPolicy", nonRetryable: true);
}
