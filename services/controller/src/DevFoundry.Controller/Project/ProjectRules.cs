using System.Text;
using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>
/// One mechanical rule a project sets for itself: lines a change may not add in the files it names. Checked by the controller on
/// the diff, so it cannot be talked around the way a line in a prompt can. <see cref="Block"/> sends the change back to the developer;
/// otherwise the reviewers are told about it.
/// </summary>
public sealed record ProjectRule(string Id, IReadOnlyList<string> Files, string Forbid, string Message, bool Block = true);

public sealed record RuleHit(ProjectRule Rule, string Path, int Line, string Text);

public static class ProjectRules
{
    public const int MaxRules = 20;
    public const int MaxPatternLength = 300;
    public const int MaxMessageLength = 300;
    private const int MaxHitsPerRule = 3;

    /// <summary>
    /// The pattern is the project's own text, so it is matched in linear time (no backtracking, so no catastrophic patterns) and with a
    /// time limit as well. That rules out lookarounds and backreferences, which is stated in the error.
    /// </summary>
    public static Regex? Compile(string pattern, out string error)
    {
        error = "";
        try
        {
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            error = $"is not a usable regular expression (lookarounds and backreferences are not supported): {exception.Message.Split('\n')[0]}";
            return null;
        }
    }

    /// <summary>Lines the diff adds, in files a rule covers, that the rule forbids. Only added lines count, so old code is not blamed.</summary>
    public static IReadOnlyList<RuleHit> Check(IReadOnlyList<ProjectRule> rules, string diff)
    {
        var hits = new List<RuleHit>();
        if (rules.Count == 0)
        {
            return hits;
        }
        var compiled = rules.Select(rule => (Rule: rule, Regex: Compile(rule.Forbid, out _))).Where(r => r.Regex is not null).ToList();
        var counts = new Dictionary<string, int>();
        foreach (var file in UnifiedDiff.Split(diff))
        {
            var applicable = compiled.Where(r => Globs.IsMatchAny(file.Path, r.Rule.Files)).ToList();
            if (applicable.Count == 0)
            {
                continue;
            }
            var line = 0;
            foreach (var text in file.Text.Split('\n'))
            {
                if (text.StartsWith("@@", StringComparison.Ordinal))
                {
                    var plus = text.IndexOf('+');
                    var end = plus < 0 ? -1 : text.IndexOfAny([',', ' '], plus);
                    line = plus >= 0 && end > plus && int.TryParse(text[(plus + 1)..end], out var start) ? start - 1 : 0;
                    continue;
                }
                if (text.StartsWith("+++", StringComparison.Ordinal) || text.StartsWith("---", StringComparison.Ordinal) || text.StartsWith("diff ", StringComparison.Ordinal))
                {
                    continue;
                }
                if (text.StartsWith('+'))
                {
                    line++;
                    foreach (var (rule, regex) in applicable)
                    {
                        counts.TryGetValue(rule.Id, out var seen);
                        bool matched;
                        try
                        {
                            matched = regex!.IsMatch(text[1..]);
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            matched = false;
                        }
                        if (matched && seen < MaxHitsPerRule)
                        {
                            counts[rule.Id] = seen + 1;
                            hits.Add(new RuleHit(rule, file.Path, line, text[1..].Trim()));
                        }
                    }
                }
                else if (!text.StartsWith('-'))
                {
                    line++; // a context line exists in the new file too
                }
            }
        }
        return hits;
    }

    /// <summary>The rules that block, written for the agent that writes code, so it does not find them out one round at a time. Null when none.</summary>
    public static string? PromptSection(IReadOnlyList<ProjectRule>? rules)
    {
        var blocking = rules?.Where(rule => rule.Block).ToList();
        if (blocking is not { Count: > 0 })
        {
            return null;
        }
        var text = new StringBuilder("The project's own rules. A line you add that breaks one is rejected, so avoid it from the start:");
        foreach (var rule in blocking)
        {
            text.Append($"\n- {rule.Id} (in {string.Join(", ", rule.Files)}): {rule.Message}");
        }
        return text.ToString();
    }

    /// <summary>What goes back to the developer for rules that block. Null when none do.</summary>
    public static string? BlockingFeedback(IEnumerable<RuleHit> hits)
    {
        var blocking = hits.Where(hit => hit.Rule.Block).ToList();
        return blocking.Count == 0 ? null : Describe("The project's own rules (in .factory/project.json) are broken by the lines you added; fix them:", blocking);
    }

    /// <summary>Rules that only warn, written for the reviewers. Null when none matched.</summary>
    public static string? WarningNotes(IEnumerable<RuleHit> hits)
    {
        var warnings = hits.Where(hit => !hit.Rule.Block).ToList();
        return warnings.Count == 0 ? null : Describe("Project rules that only warn were matched by added lines; decide whether they matter:", warnings);
    }

    private static string Describe(string heading, IReadOnlyList<RuleHit> hits)
    {
        var text = new StringBuilder(heading);
        foreach (var hit in hits.Take(10))
        {
            var shown = hit.Text.Length > 100 ? hit.Text[..100] + "..." : hit.Text;
            text.Append($"\n- rule \"{hit.Rule.Id}\" at {hit.Path}:{hit.Line}: {hit.Rule.Message} (added line: {shown})");
        }
        return text.ToString();
    }
}
