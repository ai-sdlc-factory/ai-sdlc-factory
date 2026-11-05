using System.Diagnostics;
using Temporalio.Exceptions;

namespace DevFoundry.Controller;

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

    private async Task<string> GitAsync(
        IReadOnlyList<string> arguments, string workingDirectory, bool useToken, CancellationToken cancellationToken, bool allowFailure = false)
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
