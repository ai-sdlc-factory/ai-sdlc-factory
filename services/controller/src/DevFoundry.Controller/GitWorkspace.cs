using System.Diagnostics;
using System.Text.RegularExpressions;
using Temporalio.Exceptions;

namespace DevFoundry.Controller;

public sealed record WorkspaceRequest(string Owner, string Repo, string Name, string Branch);

public sealed record WorkspaceInfo(string Name, string Path, string Branch, string DefaultBranch, string HeadCommit);

/// <summary>
/// Creates and removes the per-run git checkouts that sandboxes mount. Only the controller does
/// this, because only it holds the Forgejo token; the token is handed to git through the
/// environment, so it appears in no argument list and is never written to the repository config.
/// </summary>
public sealed partial class GitWorkspace(WorkerSettings settings, Func<string, string, string>? remoteUrl = null)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(3);

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,100}\z")]
    private static partial Regex SafeSegment();

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9._-]{0,127}\z")]
    private static partial Regex WorkspaceName();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._/-]{0,100}\z")]
    private static partial Regex BranchName();

    public async Task<WorkspaceInfo> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        if (!SafeSegment().IsMatch(request.Owner) || !SafeSegment().IsMatch(request.Repo)
            || request.Repo.Contains("..") || request.Owner.Contains("..")
            || !WorkspaceName().IsMatch(request.Name) || request.Name.Contains("..")
            || !BranchName().IsMatch(request.Branch) || request.Branch.Contains(".."))
        {
            throw new ApplicationFailureException("Workspace request has an invalid name.", nonRetryable: true);
        }

        var path = $"{root}/{request.Name}";
        // A retry after a half-finished attempt starts from a clean directory.
        DeleteDirectory(path);
        Directory.CreateDirectory(path);

        var url = remoteUrl?.Invoke(request.Owner, request.Repo)
            ?? $"{settings.ForgejoUrl.TrimEnd('/')}/{request.Owner}/{request.Repo}.git";
        await GitAsync(["clone", "--quiet", url, "."], path, useToken: true, cancellationToken);
        await GitAsync(["checkout", "--quiet", "-b", request.Branch], path, useToken: false, cancellationToken);
        await GitAsync(["config", "user.name", "Dev Foundry"], path, useToken: false, cancellationToken);
        await GitAsync(["config", "user.email", "factory-bot@dev-foundry.test"], path, useToken: false, cancellationToken);

        var head = (await GitAsync(["rev-parse", "HEAD"], path, useToken: false, cancellationToken)).Trim();
        var defaultBranch = (await GitAsync(["rev-parse", "--abbrev-ref", "origin/HEAD"], path, useToken: false, cancellationToken))
            .Trim().Replace("origin/", "", StringComparison.Ordinal);
        return new WorkspaceInfo(request.Name, path, request.Branch, defaultBranch, head);
    }

    public Task CleanupAsync(string name)
    {
        var root = RequireRoot();
        if (!WorkspaceName().IsMatch(name) || name.Contains(".."))
        {
            throw new ApplicationFailureException("Workspace name is invalid.", nonRetryable: true);
        }
        DeleteDirectory($"{root}/{name}");
        return Task.CompletedTask;
    }

    private string RequireRoot()
    {
        var root = settings.WorkspaceRoot.TrimEnd('/');
        if (!root.StartsWith('/') || root.Length < 2)
        {
            throw new ApplicationFailureException(
                "FACTORY_WORKSPACE_ROOT is not configured for the controller.", nonRetryable: true);
        }
        return root;
    }

    private static void DeleteDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null)
        {
            directory.Delete(); // remove the link itself, never what it points to
        }
        else if (directory.Exists)
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task<string> GitAsync(
        IReadOnlyList<string> arguments, string workingDirectory, bool useToken, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        if (useToken && !string.IsNullOrWhiteSpace(settings.ForgejoToken))
        {
            start.Environment["GIT_CONFIG_COUNT"] = "1";
            start.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            start.Environment["GIT_CONFIG_VALUE_0"] = $"Authorization: token {settings.ForgejoToken}";
        }

        using var process = Process.Start(start)
            ?? throw new ApplicationFailureException("Could not start git.", nonRetryable: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            // The token is passed by environment, so it cannot appear in git's own messages.
            var message = (await error).Trim();
            throw new ApplicationFailureException(
                $"git {arguments[0]} failed: {(message.Length > 500 ? message[..500] : message)}",
                nonRetryable: message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("403") || message.Contains("401"));
        }
        return await output;
    }
}
