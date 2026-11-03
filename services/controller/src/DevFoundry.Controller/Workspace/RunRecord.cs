using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>
/// One step of a run worth keeping: what an agent was told and what it answered, or which project check ran and
/// what it printed. The workflow builds these; the controller writes them to the host and, when the project asked
/// for it, commits them with the work.
/// </summary>
public sealed record RecordEntry(
    string Name,
    int Sequence,
    string Kind,
    string Slug,
    string Title,
    int Attempt,
    string StartedAt,
    double Seconds,
    string Outcome,
    long ExitCode,
    bool TimedOut,
    string Input,
    string Output,
    string? Problem = null,
    string? Model = null,
    TokenUsage? Usage = null);

/// <summary>Where in the repository the record goes, and how the run ended, for the summary file.</summary>
public sealed record RunRecordSpec(string Directory, string Heading, string Outcome);

/// <summary>Turns recorded steps into the files committed under <c>.factory/runs/</c>.</summary>
public static partial class RunRecord
{
    public const string Root = ".factory/runs";
    public const int MaxInputChars = 100_000;
    public const int MaxOutputChars = 60_000;

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,63}\z")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"\A\.factory/runs/[a-z0-9][a-z0-9._-]{0,100}\z")]
    private static partial Regex DirectoryPattern();

    public static bool IsValidSlug(string slug) => SlugPattern().IsMatch(slug);

    public static bool IsValidDirectory(string directory) => DirectoryPattern().IsMatch(directory) && !directory.Contains("..");

    public static string FileName(RecordEntry entry) => $"{entry.Sequence:000}-{entry.Slug}.md";

    /// <summary>The directory a run's record goes to: the ticket plus the start of the run's id, so a re-run never overwrites.</summary>
    public static string DirectoryFor(int issueNumber, string runId) =>
        $"{Root}/issue-{issueNumber}-{new string(runId.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray()).ToLowerInvariant()}";

    /// <summary>One step as a readable markdown file. Lines that look like credentials are replaced, never written.</summary>
    public static string Render(RecordEntry entry, IEnumerable<string> knownSecrets)
    {
        var secrets = knownSecrets.ToList();
        var text = new StringBuilder();
        text.AppendLine($"# {entry.Title}");
        text.AppendLine();
        text.AppendLine($"- step: {entry.Sequence}, attempt {entry.Attempt}");
        text.AppendLine($"- started: {entry.StartedAt}");
        text.AppendLine($"- duration: {entry.Seconds:0.#} s");
        text.AppendLine($"- outcome: {entry.Outcome}");
        text.AppendLine($"- exit code: {entry.ExitCode}{(entry.TimedOut ? " (timed out)" : "")}");
        if (entry.Model is not null)
        {
            text.AppendLine($"- model: {OneLine(entry.Model)} (as configured for this role)");
        }
        if (entry.Usage is { } usage)
        {
            text.AppendLine($"- tokens: {Describe(usage)}");
            text.AppendLine($"- cost: {Cost(usage.Cost)} (as reported by OpenCode)");
        }
        if (entry.Problem is not null)
        {
            text.AppendLine($"- problem: {OneLine(SecretScan.Redact(entry.Problem, secrets))}");
        }
        text.AppendLine();
        text.AppendLine(entry.Kind == "agent" ? "## Input (the prompt)" : "## Command");
        text.AppendLine();
        AppendBlock(text, Limit(SecretScan.Redact(entry.Input, secrets), MaxInputChars, keepEnd: false));
        text.AppendLine();
        text.AppendLine(entry.Kind == "agent" ? "## Output (the answer)" : "## Output (the end of it)");
        text.AppendLine();
        AppendBlock(text, Limit(SecretScan.Redact(entry.Output, secrets), MaxOutputChars, keepEnd: entry.Kind != "agent"));
        return text.ToString();
    }

    /// <summary>The summary of a whole run: how it ended, and one line per recorded step with a link to its file.</summary>
    public static string Summary(RunRecordSpec spec, IReadOnlyList<RecordEntry> entries)
    {
        var text = new StringBuilder();
        text.AppendLine($"# {OneLine(spec.Heading)}");
        text.AppendLine();
        text.AppendLine(spec.Outcome);
        text.AppendLine();
        text.AppendLine("Written by the factory from what it sent to each agent and received back. The agents did not write this file.");
        text.AppendLine();
        text.AppendLine("| Step | What | Attempt | Outcome | Seconds |");
        text.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var entry in entries.OrderBy(e => e.Sequence))
        {
            text.AppendLine($"| {entry.Sequence} | [{entry.Title}]({FileName(entry)}) | {entry.Attempt} | {entry.Outcome} | {entry.Seconds:0.#} |");
        }

        var used = entries.Where(e => e.Usage is not null).ToList();
        if (used.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Usage");
            text.AppendLine();
            text.AppendLine("Tokens as reported by OpenCode, by role (all attempts). Cost is what OpenCode reports; it is 0 with a ChatGPT login. The full list is in [usage.json](usage.json).");
            text.AppendLine();
            text.AppendLine("| Role | Model | Runs | Input | Output | Reasoning | Cache read | Total | Cost |");
            text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var group in used.GroupBy(e => e.Slug).OrderBy(g => g.Min(e => e.Sequence)))
            {
                var sum = TokenUsage.Sum(group.Select(e => e.Usage!));
                text.AppendLine($"| {group.Key} | {group.First().Model ?? "unknown"} | {group.Count()} | {sum.Input} | {sum.Output} | {sum.Reasoning} | {sum.CacheRead} | {sum.Total} | {Cost(sum.Cost)} |");
            }
            var all = TokenUsage.Sum(used.Select(e => e.Usage!));
            text.AppendLine($"| **all** | | {used.Count} | {all.Input} | {all.Output} | {all.Reasoning} | {all.CacheRead} | **{all.Total}** | {Cost(all.Cost)} |");
            if (all.Partial)
            {
                text.AppendLine();
                text.AppendLine("Some output was cut, so the numbers are a lower bound.");
            }
        }
        return text.ToString();
    }

    /// <summary>The tokens and cost of every recorded step and the totals by role, for tools and later analysis.</summary>
    public static string UsageJson(IReadOnlyList<RecordEntry> entries)
    {
        var used = entries.Where(e => e.Usage is not null).OrderBy(e => e.Sequence).ToList();
        object Tokens(TokenUsage u) => new
        {
            input = u.Input, output = u.Output, reasoning = u.Reasoning, cache_read = u.CacheRead, cache_write = u.CacheWrite,
            total = u.Total, cost = u.Cost, model_steps = u.Steps, partial = u.Partial,
        };
        return JsonSerializer.Serialize(
            new
            {
                note = "Tokens and cost as reported by OpenCode. Cost is 0 with a ChatGPT login. The model is the one configured for the role.",
                total = Tokens(TokenUsage.Sum(used.Select(e => e.Usage!))),
                by_role = used.GroupBy(e => e.Slug).ToDictionary(g => g.Key, g => new { model = g.First().Model, runs = g.Count(), usage = Tokens(TokenUsage.Sum(g.Select(e => e.Usage!))) }),
                steps = used.Select(e => new { step = e.Sequence, role = e.Slug, model = e.Model, attempt = e.Attempt, seconds = e.Seconds, usage = Tokens(e.Usage!) }),
            },
            new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    public static bool HasUsage(IReadOnlyList<RecordEntry> entries) => entries.Any(e => e.Usage is not null);

    private static string Describe(TokenUsage u) =>
        $"{u.Total} in total (input {u.Input}, output {u.Output}, reasoning {u.Reasoning}, cache read {u.CacheRead}, cache write {u.CacheWrite}){(u.Partial ? ", at least: output was cut" : "")}";

    private static string Cost(double cost) => cost.ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>The machine-readable index: every step without its (large) input and output.</summary>
    public static string Index(IReadOnlyList<RecordEntry> entries) =>
        JsonSerializer.Serialize(
            entries.OrderBy(e => e.Sequence).Select(e => new
            {
                step = e.Sequence, kind = e.Kind, title = e.Title, attempt = e.Attempt, started = e.StartedAt,
                seconds = e.Seconds, outcome = e.Outcome, exit_code = e.ExitCode, timed_out = e.TimedOut,
                file = FileName(e), model = e.Model, tokens = e.Usage?.Total, cost = e.Usage?.Cost,
            }),
            new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static void AppendBlock(StringBuilder text, string content)
    {
        // A fence one longer than any run of backticks inside, so the content can never close it early.
        var longest = BacktickRun().Matches(content).Select(match => match.Length).DefaultIfEmpty(0).Max();
        var fence = new string('`', Math.Max(3, longest + 1));
        text.AppendLine(fence + "text");
        text.AppendLine(content.TrimEnd('\n'));
        text.AppendLine(fence);
    }

    [GeneratedRegex("`+")]
    private static partial Regex BacktickRun();

    private static string Limit(string text, int max, bool keepEnd)
    {
        if (text.Length <= max)
        {
            return text;
        }
        return keepEnd
            ? $"[the first {text.Length - max} characters were left out]\n" + text[^max..]
            : text[..max] + $"\n[{text.Length - max} more characters left out]";
    }

    private static string OneLine(string text) => text.Replace('\n', ' ').Replace('\r', ' ').Trim();
}
