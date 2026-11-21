using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public abstract partial class FactoryWorkflowBase
{
    /// <summary>
    /// Runs one command in the sandbox without model access: the project's tooling may fetch packages, nothing else.
    /// Returns null when the run could not be made at all (the sandbox itself failed).
    /// </summary>
    protected static async Task<SandboxRunResult?> RunCommandAsync(
        string image, WorkspaceInfo workspace, IReadOnlyList<string> command, int timeoutSeconds, string network, bool writable, string role)
    {
        try
        {
            return await Workflow.ExecuteActivityAsync<SandboxRunResult>(
                SandboxNames.ActivityName,
                [new SandboxRunRequest(image, workspace.Name, writable, command, timeoutSeconds, Network: network, Role: role)],
                ActivityPolicy.Sandbox(TimeSpan.FromSeconds(timeoutSeconds + 300), attempts: 1));
        }
        catch (ActivityFailureException)
        {
            return null;
        }
    }

    /// <summary>A command that did not start (not found, not executable), timed out, or never ran is not evidence of anything.</summary>
    protected static bool Inconclusive(SandboxRunResult? result) =>
        result is null || result.TimedOut || result.ExitCode is 126 or 127;

    protected static string CombinedOutput(SandboxRunResult result) =>
        string.Join('\n', new[] { result.Stdout, result.Stderr }.Where(part => part.Length > 0));

    /// <summary>The SHA-256 of each file, read inside the sandbox; empty when it could not be done.</summary>
    protected static async Task<IReadOnlyDictionary<string, string>> FingerprintAsync(
        string image, WorkspaceInfo workspace, IReadOnlyList<string> files)
    {
        var result = await RunCommandAsync(image, workspace, ["sha256sum", "--", .. files], 60, SandboxNetworks.None, false, "fingerprint");
        var prints = new Dictionary<string, string>();
        if (result is null || result.ExitCode != 0)
        {
            return prints;
        }
        foreach (var line in result.Stdout.Split('\n'))
        {
            // "<64 hex>  <path>" (a '*' marks binary mode)
            if (line.Length > 66 && line[..64].All(Uri.IsHexDigit) && line[64] == ' ')
            {
                prints[line[66..].Trim()] = line[..64];
            }
        }
        return prints;
    }

    /// <summary>Why the reproduction test was changed, or null when it is exactly as the reproducer left it.</summary>
    protected static async Task<string?> CheckReproductionTestUntouchedAsync(string image, WorkspaceInfo workspace, BugBrief bug)
    {
        if (bug.Fingerprints.Count == 0)
        {
            return null; // the original could not be fingerprinted, so there is nothing to compare with
        }
        var now = await FingerprintAsync(image, workspace, bug.TestFiles);
        var changed = bug.TestFiles.Where(file => !now.TryGetValue(file, out var print) || print != bug.Fingerprints.GetValueOrDefault(file)).ToList();
        return changed.Count == 0
            ? null
            : $"You changed or removed the reproduction test ({string.Join(", ", changed)}). Restore it exactly and fix the production code instead; the test defines the bug.";
    }

    /// <summary>Why the reproduction test still fails after the fix, or null when it passes.</summary>
    protected static async Task<string?> CheckReproductionPassesAsync(string image, WorkspaceInfo workspace, BugBrief bug)
    {
        var result = await RunCommandAsync(image, workspace, bug.TestCommand, 600, SandboxNetworks.Packages, true, "green-check");
        if (result is { ExitCode: 0, TimedOut: false })
        {
            return null;
        }
        var detail = result is null ? "the command could not be run" : result.TimedOut ? "it timed out" : $"exit code {result.ExitCode}";
        var output = result is null ? "" : CombinedOutput(result);
        return $"the reproduction test `{string.Join(' ', bug.TestCommand)}` still does not pass ({detail}).{(output.Length > 0 ? "\n" + Tail(output, 1500) : "")}";
    }

    protected static string Tail(string text, int max) => text.Length <= max ? text.Trim() : "..." + text[^max..].Trim();
}
