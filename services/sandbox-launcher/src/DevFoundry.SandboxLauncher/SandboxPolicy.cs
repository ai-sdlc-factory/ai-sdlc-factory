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
    bool NeedsModelAuth);

/// <summary>
/// Everything a requester is allowed to influence, and nothing else. The caller is the trusted
/// controller, but the policy still treats the request as untrusted: the launcher holds Docker
/// access, so it is the last line of defence.
/// </summary>
public static partial class SandboxPolicy
{
    public const int MaxArguments = 64;
    public const int MaxArgumentLength = 32 * 1024;
    private static readonly string[] AllowedEnvPrefixes = ["OPENCODE_", "FACTORY_"];

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9._-]{0,127}\z")]
    private static partial Regex WorkspaceName();

    [GeneratedRegex(@"\A[A-Z][A-Z0-9_]{0,63}\z")]
    private static partial Regex EnvName();

    public static ValidatedRun Validate(
        SandboxRunRequest request, SandboxSettings settings, Func<string, bool> workspaceExists)
    {
        if (!settings.Images.TryGetValue(request.Image ?? "", out var image))
        {
            throw Reject($"Unknown sandbox image alias '{request.Image}'.");
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

        return new ValidatedRun(image, path, request.WorkspaceWritable, request.Command, timeout, env, request.NeedsModelAuth);
    }

    // Policy violations will not fix themselves, so they are never retried.
    private static ApplicationFailureException Reject(string message) =>
        new($"Sandbox request rejected: {message}", errorType: "SandboxPolicy", nonRetryable: true);
}
