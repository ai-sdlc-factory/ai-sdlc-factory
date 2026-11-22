using System.Diagnostics;
using Temporalio.Exceptions;

namespace AiSdlcFactory.Controller;

public sealed partial class GitWorkspace
{
    private static async Task RunAsync(string program, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { WorkingDirectory = workingDirectory, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new ApplicationFailureException($"Could not start {program}.", nonRetryable: true);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new ApplicationFailureException($"{program} failed: {(await error).Trim()}");
        }
    }

    /// <summary>The host a pristine clone came from (the default one for a clone without the marker, such as one made before hosts existed).</summary>
    private static string HostOfClone(string directory)
    {
        var marker = $"{directory}/.git/{HostMarker}";
        return File.Exists(marker) && File.ReadAllText(marker).Trim() is { Length: > 0 } name ? name : RepoNames.DefaultHost;
    }

    /// <summary>The header git sends to a host (its kind decides the scheme), or empty when it has no token (or is not configured).</summary>
    private string AuthorizationFor(string host) => settings.Hosts.FirstOrDefault(known => known.Name == host)?.GitAuthorization ?? "";

    private async Task<string> GitAsync(
        IReadOnlyList<string> arguments, string workingDirectory, bool useToken, CancellationToken cancellationToken, bool allowFailure = false, string? tokenHost = null)
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
        if (useToken && AuthorizationFor(tokenHost ?? HostOfClone(workingDirectory)) is { Length: > 0 } authorization)
        {
            start.Environment["GIT_CONFIG_COUNT"] = "1";
            start.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            start.Environment["GIT_CONFIG_VALUE_0"] = authorization;
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

        if (process.ExitCode != 0 && allowFailure)
        {
            return "";
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
