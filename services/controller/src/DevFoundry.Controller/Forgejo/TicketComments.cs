using System.Text;

namespace DevFoundry.Controller;

/// <summary>Formats what the factory says on a ticket. Model text is untrusted, so it is made inert.</summary>
public static class TicketComments
{
    public static string Analysis(AnalystVerdict verdict)
    {
        var text = new StringBuilder();
        switch (verdict.Verdict)
        {
            case AnalystVerdict.Pass:
                text.AppendLine("**Analysis: ready to build**");
                break;
            case AnalystVerdict.NeedsClarification:
                text.AppendLine("**Analysis: I need answers before building**");
                break;
            default:
                text.AppendLine("**Analysis: not buildable as written**");
                break;
        }
        text.AppendLine();
        text.AppendLine(Inert(verdict.Summary));

        if (verdict.AcceptanceCriteria.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Acceptance criteria**");
            foreach (var criterion in verdict.AcceptanceCriteria)
            {
                text.AppendLine($"- {Inert(criterion)}");
            }
        }
        if (verdict.Questions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Questions**");
            for (var i = 0; i < verdict.Questions.Count; i++)
            {
                text.AppendLine($"{i + 1}. {Inert(verdict.Questions[i])}");
            }
            text.AppendLine();
            text.AppendLine("Reply on this ticket and I will continue.");
        }

        var c = verdict.Complexity;
        text.AppendLine();
        text.Append($"Estimate: about {c.FilesEstimate} file(s); several projects: {YesNo(c.MultipleProjects)}; " +
                    $"public API change: {YesNo(c.PublicApiChange)}; data model change: {YesNo(c.DataModelChange)}; " +
                    $"new dependency: {YesNo(c.NewDependency)}.");
        return text.ToString().TrimEnd();
    }

    public static string Coverage(CoverageReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(report.Verdict switch
        {
            CoverageVerdicts.FullyCovered => "**Coverage check: already implemented**",
            CoverageVerdicts.PartlyCovered => "**Coverage check: partly implemented**",
            _ => "**Coverage check: not implemented yet**",
        });
        text.AppendLine();
        text.AppendLine(Inert(report.Summary));
        text.AppendLine();
        foreach (var criterion in report.Criteria)
        {
            text.Append($"- **{criterion.Status}**: {Inert(criterion.Criterion)}");
            if (criterion.Evidence.Count > 0)
            {
                // Evidence paths were validated to a safe character set, so they are safe in code spans.
                text.Append(" (" + string.Join(", ", criterion.Evidence.Select(path => $"`{path}`")) + ")");
            }
            if (criterion.Note.Length > 0)
            {
                text.Append($" - {Inert(criterion.Note)}");
            }
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    public static string Design(ArchitectDesign design)
    {
        var text = new StringBuilder();
        text.AppendLine(design.Verdict switch
        {
            ArchitectDesign.Pass => "**Design: ready to build**",
            ArchitectDesign.NeedsDecision => "**Design: a decision is needed**",
            _ => "**Design: not feasible as asked**",
        });
        text.AppendLine();
        text.AppendLine(Inert(design.Summary));
        if (design.Approach.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("**Approach**");
            text.AppendLine(Inert(design.Approach));
        }
        if (design.Components.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Components**");
            foreach (var component in design.Components)
            {
                // Paths were validated to a safe character set, so they are safe in code spans.
                text.AppendLine($"- `{component.Path}`: {Inert(component.Change)}");
            }
        }
        if (design.Risks.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Risks**");
            foreach (var risk in design.Risks)
            {
                text.AppendLine($"- {Inert(risk)}");
            }
        }
        if (design.Guidance.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Guidance for the build**");
            foreach (var item in design.Guidance)
            {
                text.AppendLine($"- {Inert(item)}");
            }
        }
        if (design.Questions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Questions**");
            for (var i = 0; i < design.Questions.Count; i++)
            {
                text.AppendLine($"{i + 1}. {Inert(design.Questions[i])}");
            }
            text.AppendLine();
            text.AppendLine("Reply on this ticket and I will continue.");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>What the ticket is told when a run stops without a pull request, and where the work and the record are.</summary>
    public static string Stopped(string status, RescueResult rescue)
    {
        var reason = status switch
        {
            "build_failed" => "the work did not get through the checks and the review in the allowed rounds",
            "build_blocked" => "the developer reported that it could not continue",
            "review_failed" => "a review step could not be completed",
            "publish_failed" => "the finished work could not be published as a pull request",
            "blocked" => "OpenCode is not signed in",
            "infrastructure_failed" => "part of the factory's own machinery failed",
            "error" => "an unexpected error",
            _ => status,
        };
        var text = new StringBuilder();
        text.AppendLine($"**The factory stopped: {reason}**");
        text.AppendLine();
        if (rescue.Saved)
        {
            text.AppendLine($"What it had written so far is saved on the branch `{Inert(rescue.Branch ?? "")}` ({rescue.FileCount} file(s)).");
            text.AppendLine(status == "publish_failed"
                ? "This work passed every check and review; only publishing it as a pull request failed, so it should be ready to open as one."
                : "It is **not reviewed and the checks did not all pass**, so treat it as a starting point, not as a pull request.");
        }
        else
        {
            text.AppendLine($"Nothing was saved: {Inert(rescue.Note)}.");
        }
        if (rescue.Excluded.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(rescue.Saved ? "Left out of the branch:" : "Files with a problem:");
            foreach (var file in rescue.Excluded.Take(10))
            {
                text.AppendLine($"- {Inert(file)}");
            }
            if (rescue.Excluded.Count > 10)
            {
                text.AppendLine($"- ... and {rescue.Excluded.Count - 10} more");
            }
        }
        text.AppendLine();
        text.Append($"The full record of this run (every prompt, answer and check output) is in Temporal: {rescue.TraceUrl}. " +
                    "Temporal keeps it for a limited time (7 days unless configured otherwise).");
        return text.ToString();
    }

    public static string Review(string role, ReviewReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(report.Verdict == ReviewReport.Pass ? $"**Review ({role}): no blocking findings**" : $"**Review ({role}): changes needed**");
        text.AppendLine();
        text.AppendLine(Inert(report.Summary));
        foreach (var finding in report.Findings)
        {
            text.AppendLine($"- **{finding.Severity}**{(finding.Path is null ? "" : $" `{finding.Path}`")}: {Inert(finding.Message)}");
        }
        return text.ToString().TrimEnd();
    }

    public static string Qa(QaReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(report.Verdict == ReviewReport.Pass ? "**QA: every criterion is met**" : $"**QA: {report.NotMet.Count()} criterion not met**");
        text.AppendLine();
        text.AppendLine(Inert(report.Summary));
        foreach (var criterion in report.Criteria)
        {
            text.Append($"- **{criterion.Status.Replace('_', ' ')}**: {Inert(criterion.Criterion)}");
            if (criterion.Evidence.Count > 0)
            {
                text.Append(" (" + string.Join(", ", criterion.Evidence.Select(path => $"`{path}`")) + ")");
            }
            if (criterion.Note.Length > 0)
            {
                text.Append($" - {Inert(criterion.Note)}");
            }
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>A review or <c>/fix</c> on a pull request that is closed or merged.</summary>
    public const string RevisionClosed = "This pull request is closed or merged, so I will not change it.";

    public static string RevisionLimit(int max) =>
        $"I have already made {max} revisions on this pull request. A person should take it from here (the branch is theirs to change).";

    public const string RevisionNothingToDo =
        "I found nothing to act on. Write what to change after `/fix`, or submit a review that requests changes and has comments, then ask again with `/fix`.";

    public const string RevisionBusy =
        "A revision of this pull request is already running and could not be told about this. When it has finished, ask again with `/fix` and what to change.";

    public const string RevisionQueued =
        "A revision of this pull request is running. I noted this and will look at it as a follow-up when that one is done.";

    /// <summary>Hidden marker on a notice about a comment that could not be used, so the same notice is not repeated.</summary>
    public const string NoticeMarker = "<!-- dev-foundry:notice -->";

    /// <summary>A comment arrived while the build was running, which no longer takes new input.</summary>
    public const string CommentTooLate =
        "I saw your comment, but the build has already started and it cannot take new input, so it was not used. " +
        "To change what is built, update the ticket text, then close and reopen the ticket; that starts a new run.";

    /// <summary>Comments that arrived but that no stage ever asked for.</summary>
    public static string CommentsNotUsed(int count) =>
        $"{count} comment{(count == 1 ? "" : "s")} arrived during this run, but no stage was waiting for an answer, so " +
        $"{(count == 1 ? "it was" : "they were")} not used. To have {(count == 1 ? "it" : "them")} taken into account, add " +
        "the details to the ticket text, then close and reopen the ticket.";

    /// <summary>Added to a failure caused by the factory's own machinery rather than by the ticket or the agent's work.</summary>
    public const string RetryHint =
        " This looks like a problem with the factory itself, not with your ticket. Comment `/retry` to run it again.";

    /// <summary>A comment on a feature ticket that has no running workflow.</summary>
    public const string NoRunActive =
        "No factory run is active for this ticket (it has finished, or was never started), so this comment was not used. " +
        "To run it again, comment `/retry` (or close and reopen the ticket); update the ticket text first if the requirements changed.";

    /// <summary>`/retry` on a ticket whose run is still going.</summary>
    public const string RetryWhileRunning =
        "A factory run is still active for this ticket, so `/retry` was not needed. It works once the run has finished.";

    /// <summary>Posted instead of the triage comment when a ticket runs in quick mode.</summary>
    public static string QuickMode(string reason, IReadOnlyList<string> skippedDesignReasons)
    {
        var text = new StringBuilder();
        text.AppendLine("**Quick mode**");
        text.AppendLine();
        text.AppendLine($"Because {reason}, I skip the coverage check, the design review and QA. The analyst, the developer, the project's checks and the reviewers still run.");
        if (skippedDesignReasons.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"Triage would have asked for a design review ({string.Join("; ", skippedDesignReasons)}); quick mode builds without it.");
        }
        text.AppendLine();
        text.Append($"For the full process, add the `{WorkMode.FullLabel}` label (or remove `{WorkMode.QuickLabel}`), then close and reopen the ticket.");
        return text.ToString();
    }

    public static string TriageDecision(TriageDecision decision) =>
        decision.NeedsArchitect
            ? "**Triage: design review needed before building**\n\nReasons: " + string.Join("; ", decision.Reasons) + "."
            : "**Triage: small change, no design review needed**\n\nNo rule asked for one: few files, one project, no API, data model or dependency change.";

    /// <summary>Stops text from notifying people (@name) or starting its own lines/blocks.</summary>
    public static string Inert(string value) =>
        value.Replace("@", "@​").Replace("\r", " ").Replace("\n", " ").Replace("`", "'").Trim();

    private static string YesNo(bool value) => value ? "yes" : "no";
}
