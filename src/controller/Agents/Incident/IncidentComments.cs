using System.Text;

namespace AiSdlcFactory.Controller;

/// <summary>What the incident ticket is told. Model text is made inert before it is posted.</summary>
public static class IncidentComments
{
    private static string Inert(string value) => TicketComments.Inert(value);

    public static string Assessment(IncidentAssessment assessment)
    {
        var text = new StringBuilder();
        text.AppendLine(assessment.Verdict switch
        {
            IncidentAssessment.Pass => $"**Incident assessment: severity {assessment.Severity}**",
            IncidentAssessment.NeedsClarification => "**Incident assessment: I need more information**",
            _ => "**Incident assessment: this is not an incident I can help with**",
        });
        text.AppendLine();
        text.AppendLine(Inert(assessment.Summary));
        text.AppendLine();
        text.AppendLine($"- **Impact:** {Inert(assessment.Impact)}");
        foreach (var line in assessment.Timeline)
        {
            text.AppendLine($"- Timeline: {Inert(line)}");
        }
        foreach (var gap in assessment.EvidenceGaps)
        {
            text.AppendLine($"- Missing: {Inert(gap)}");
        }
        AppendQuestions(text, assessment.Questions);
        return text.ToString().TrimEnd();
    }

    public static string Investigation(Investigation investigation)
    {
        var text = new StringBuilder();
        text.AppendLine(investigation.Verdict == Controller.Investigation.Pass ? "**Investigation: likely causes**" : "**Investigation: I need more information**");
        text.AppendLine();
        text.AppendLine(Inert(investigation.Summary));
        foreach (var (hypothesis, i) in investigation.Hypotheses.Select((h, i) => (h, i + 1)))
        {
            text.AppendLine();
            text.AppendLine($"{i}. **{hypothesis.Confidence} confidence{(hypothesis.Verified ? "" : ", not backed by anything I could find")}:** {Inert(hypothesis.Cause)}");
            if (hypothesis.Evidence.Count > 0)
            {
                text.AppendLine($"   Files: {string.Join(", ", hypothesis.Evidence.Select(path => $"`{Inert(path)}`"))}");
            }
            if (hypothesis.Commits.Count > 0)
            {
                text.AppendLine($"   Commits: {string.Join(", ", hypothesis.Commits.Select(commit => $"`{Inert(commit)}`"))}");
            }
        }
        AppendQuestions(text, investigation.Questions);
        return text.ToString().TrimEnd();
    }

    public static string Options(Mitigation mitigation)
    {
        var text = new StringBuilder();
        text.AppendLine(mitigation.Verdict == Mitigation.Pass ? "**Options to reduce the harm**" : "**Options: I need more information**");
        text.AppendLine();
        text.AppendLine(Inert(mitigation.Summary));
        foreach (var (option, i) in mitigation.Options.Select((o, i) => (o, i + 1)))
        {
            text.AppendLine();
            text.AppendLine($"**Option {i}{(i == mitigation.Recommended ? " (recommended)" : "")}: {Inert(option.Title)}** ({option.Kind.Replace('_', ' ')}, risk {option.Risk}, {(option.Reversible ? "reversible" : "not easily reversible")})");
        }
        if (mitigation.Options.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("I do not change anything in production myself. Reply with:");
            text.AppendLine("- `/option N` to get the checklist for an option;");
            text.AppendLine("- `/hotfix` to open a bug ticket for a code fix;");
            text.AppendLine("- `/resolved` when the incident is over, and I will draft a postmortem as a pull request;");
            text.AppendLine("- or any other comment with new information, and I will look again.");
        }
        AppendQuestions(text, mitigation.Questions);
        return text.ToString().TrimEnd();
    }

    public static string Runbook(int number, MitigationOption option)
    {
        var text = new StringBuilder();
        text.AppendLine($"**Checklist for option {number}: {Inert(option.Title)}**");
        text.AppendLine();
        text.AppendLine("A person carries these out; I do not have access to production.");
        text.AppendLine();
        foreach (var step in option.Steps)
        {
            text.AppendLine($"- [ ] {Inert(step)}");
        }
        text.AppendLine();
        text.AppendLine($"**How to tell it worked:** {Inert(option.Verify)}");
        text.AppendLine($"**Risk:** {option.Risk}. {(option.Reversible ? "This can be undone." : "This cannot easily be undone.")}");
        text.AppendLine();
        text.Append("Reply `/resolved` once the incident is over.");
        return text.ToString();
    }

    public static string HotfixOpened(int number, string url) =>
        $"Opened bug ticket #{number} for a code fix: {url}\n\nIt runs the bug workflow (a failing test first, then the fix, then review). A person still has to review and merge the pull request, and decide when to deploy it.";

    public const string HotfixAlreadyOpened = "A bug ticket for a code fix was already opened from this incident.";

    public static string NoSuchOption(int number, int count) =>
        $"There is no option {number}. The options are 1 to {count}.";

    public const string NothingToRefresh =
        "I have already looked again several times for this incident. Use `/option N`, `/hotfix` or `/resolved`, or close the ticket.";

    public static string PostmortemOpened(string url) =>
        $"**Draft postmortem:** {url}\n\nIt was written from this ticket's record only (comments, assessment, hypotheses, options), without logs or metrics. A person must check the facts and the action items before merging.";

    private static void AppendQuestions(StringBuilder text, IReadOnlyList<string> questions)
    {
        if (questions.Count == 0)
        {
            return;
        }
        text.AppendLine();
        text.AppendLine("**Questions**");
        for (var i = 0; i < questions.Count; i++)
        {
            text.AppendLine($"{i + 1}. {Inert(questions[i])}");
        }
        text.AppendLine();
        text.AppendLine("Reply on this ticket and I will continue.");
    }
}
