using System.Text;

namespace AiSdlcFactory.Controller;

/// <summary>What <c>/setup</c> says on the ticket and in the pull request. Text from the agent or from a check's output is made harmless before it is shown.</summary>
public static class SetupComments
{
    public const string Busy = "A setup is already running for this ticket. Its result will be posted when it is done.";

    public const string Started =
        "I am reading the repository to propose a `.factory/project.json`. I will run each check I propose in a sandbox and keep only those that pass, then open a pull request that a person must read and merge. Nothing is merged by me.";

    public const string AlreadyConfigured =
        "This repository already has a `.factory/project.json` on its default branch, so there is nothing to set up. Edit that file by hand, or remove it and comment `/setup` again.";

    private const int MaxOutput = 600;

    public static string Declined(SetupProposal proposal) =>
        $"**Setup: I could not propose settings**\n\n{TicketComments.Inert(proposal.Summary)}\n\nAdd `.factory/project.json` by hand, then close and reopen a ticket.";

    public static string NoCheckPassed(IReadOnlyList<GateResult> results) =>
        "**Setup: none of the checks I proposed passed**\n\nI ran each proposed check in the sandbox and none of them succeeded, so I opened no pull request: a settings file needs at least one working check.\n\n" +
        GateTable(results) + FailureOutput(results) +
        "\n\nWrite `.factory/project.json` by hand, with checks that pass on a clean clone, then close and reopen a ticket.";

    public static string PullRequestOpened(string url, string branch, IReadOnlyList<GateResult> results, int kept) =>
        $"**Pull request opened:** {url}\n\nSettings proposed on branch `{branch}`: {kept} of {results.Count} proposed check{(results.Count == 1 ? "" : "s")} kept.\n\n" +
        GateTable(results) + FailureOutput(results) + "\n\nA person must read and merge it; until then the factory has no settings for this repository.";

    /// <summary>The pull request's description: what was proposed, why, and what ran.</summary>
    public static string PullRequestBody(int ticket, SetupProposal proposal, IReadOnlyList<GateResult> results, string file)
    {
        var text = new StringBuilder();
        text.AppendLine($"Refs #{ticket}");
        text.AppendLine();
        text.AppendLine("**`.factory/project.json` proposed by the factory.** An agent read the repository and proposed these settings; the factory checked them with its own parser and ran every proposed check in a sandbox. " +
            "Nobody has run the application, and `verify` (if present) was **not** run. A person must read this before merging: the checks, `protected_paths` and `reviewers` shape every later run.");
        text.AppendLine();
        text.AppendLine(TicketComments.NoMentions(TicketComments.Inert(proposal.Summary)));
        text.AppendLine();
        text.AppendLine("### Why");
        foreach (var (key, reason) in proposal.Reasons)
        {
            text.AppendLine($"- `{key}`: {TicketComments.NoMentions(TicketComments.Inert(reason))}");
        }
        text.AppendLine();
        text.AppendLine("### Checks that were run");
        text.AppendLine(GateTable(results));
        if (results.Any(r => !r.Passed))
        {
            text.AppendLine();
            text.AppendLine("A check that failed is **not** in the file. If it should be there, add it back once the project passes it.");
        }
        text.AppendLine();
        text.AppendLine("### The file");
        text.AppendLine("```json");
        text.Append(file.Replace("```", "'''"));
        text.AppendLine("```");
        return text.ToString();
    }

    private static string GateTable(IReadOnlyList<GateResult> results) =>
        "| Check | Result |\n| --- | --- |\n" + string.Join('\n', results.Select(r =>
            $"| `{TicketComments.Inert(r.Name)}` | {(r.Passed ? "passed" : r.TimedOut ? "failed (timed out)" : r.ExitCode < 0 ? "could not run" : $"failed (exit code {r.ExitCode})")} |"));

    private static string FailureOutput(IReadOnlyList<GateResult> results)
    {
        var failed = results.Where(r => !r.Passed).ToList();
        if (failed.Count == 0)
        {
            return "";
        }
        var text = new StringBuilder("\n\nWhat the failed checks printed (the end of it):");
        foreach (var result in failed)
        {
            var output = SecretScan.Redact(result.Output.Length > MaxOutput ? result.Output[^MaxOutput..] : result.Output, []);
            text.Append($"\n\n`{TicketComments.Inert(result.Name)}`:\n```\n{TicketComments.NoMentions(output.Replace("```", "'''")).Trim()}\n```");
        }
        return text.ToString();
    }
}
