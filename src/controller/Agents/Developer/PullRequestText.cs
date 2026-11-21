using System.Text;

namespace DevFoundry.Controller;

/// <summary>The title and body of the pull request the factory opens.</summary>
public static class PullRequestText
{
    public static string Title(WorkRequest request)
    {
        var title = TicketComments.Inert(request.Title) is { Length: > 0 } clean ? (clean.Length > 120 ? clean[..120] : clean) : $"Ticket #{request.IssueNumber}";
        return request.IsBug && !title.StartsWith("fix", StringComparison.OrdinalIgnoreCase) ? $"Fix: {title}" : title;
    }

    public static string Body(
        WorkRequest request, DeveloperReport report, IReadOnlyList<CriterionCoverage> remaining,
        ArchitectDesign? design, IReadOnlyList<GateResult> gates, int round,
        IReadOnlyList<(string Role, ReviewReport Report)>? reviews = null, QaReport? qa = null, string? uxNote = null, string? quickReason = null, BugBrief? bug = null)
    {
        var text = new StringBuilder();
        text.AppendLine($"Closes #{request.IssueNumber}");
        text.AppendLine();
        text.AppendLine("Built by the factory from the ticket. A person must review this before it is merged.");
        text.AppendLine();
        if (quickReason is not null)
        {
            text.AppendLine("## Quick mode");
            text.AppendLine($"Because {TicketComments.Inert(quickReason)}, the coverage check, the design review and the acceptance check were skipped. The project's checks and the reviewers ran as usual.");
            text.AppendLine();
        }
        if (bug is not null)
        {
            text.AppendLine("## Bug fix");
            text.AppendLine($"- **Severity:** {bug.Severity}");
            if (bug.RootCause is not null)
            {
                text.AppendLine($"- **Root cause:** {TicketComments.Inert(bug.RootCause)}");
            }
            text.AppendLine($"- **Reproduction test:** `{TicketComments.Inert(string.Join(' ', bug.TestCommand))}` ({string.Join(", ", bug.TestFiles.Select(file => $"`{TicketComments.Inert(file)}`"))})");
            text.AppendLine($"- **Before the fix:** the test failed on the unchanged code (exit code {bug.RedExitCode}). **After the fix:** it passes, and the project's checks pass.");
            text.AppendLine();
        }
        text.AppendLine("## Summary");
        text.AppendLine(TicketComments.Inert(report.Summary));
        if (remaining.Any())
        {
            text.AppendLine();
            text.AppendLine("## Acceptance criteria");
            foreach (var criterion in remaining)
            {
                text.AppendLine($"- {TicketComments.Inert(criterion.Criterion)}");
            }
        }
        if (design is not null)
        {
            text.AppendLine();
            text.AppendLine("## Design");
            text.AppendLine(TicketComments.Inert(design.Approach));
        }
        if (report.Notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Notes from the developer");
            foreach (var note in report.Notes)
            {
                text.AppendLine($"- {TicketComments.Inert(note)}");
            }
        }
        text.AppendLine();
        if (reviews is { Count: > 0 } || qa is not null || uxNote is not null)
        {
            text.AppendLine();
            text.AppendLine("## Review");
            foreach (var (role, review) in reviews ?? [])
            {
                text.AppendLine($"- **{role}**: {TicketComments.Inert(review.Summary)}");
                foreach (var finding in review.Advice.Take(10))
                {
                    text.AppendLine($"  - {finding.Severity}{(finding.Path is null ? "" : $" `{finding.Path}`")}: {TicketComments.Inert(finding.Message)}");
                }
            }
            if (uxNote is not null)
            {
                text.AppendLine($"- **ux**: not reviewed, {TicketComments.Inert(uxNote)}");
            }
            if (qa is not null)
            {
                text.AppendLine($"- **qa**: {TicketComments.Inert(qa.Summary)}");
                foreach (var criterion in qa.Criteria)
                {
                    text.AppendLine($"  - {criterion.Status.Replace('_', ' ')}: {TicketComments.Inert(criterion.Criterion)}");
                }
            }
        }
        text.AppendLine();
        text.AppendLine("## Automated checks");
        text.AppendLine($"Passed in build round {round}: {BuildFeedback.Summary(gates)}. Changed files were also checked for protected paths and secrets.");
        return text.ToString().TrimEnd();
    }
}
