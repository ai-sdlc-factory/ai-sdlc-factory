using System.Text;

namespace AiSdlcFactory.Controller;

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
            "budget_exceeded" => "the run used its token budget",
            "usage_limit" => "the model provider's usage limit is reached",
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
        text.AppendLine(report.Verdict == ReviewReport.Pass ? "**Acceptance: every criterion is met**" : $"**Acceptance: {report.NotMet.Count()} criterion not met**");
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
    /// <summary>What a person is told when the operator's rule keeps them from steering the factory. It never names anyone, so the same text on the same ticket is posted once.</summary>
    public static string CommandRefused(CommandVerdict verdict) => verdict.Refusal switch
    {
        CommandRefusal.Denied => "I did not run this command: you may not steer the factory on this repository.",
        CommandRefusal.NotListed => "I did not run this command: on this repository only the people the operator named may steer the factory.",
        CommandRefusal.TooLittleAccess => $"I did not run this command: steering the factory on this repository needs {verdict.Needs} access to it.",
        _ => $"I did not run this command: I could not find out whether you have {(verdict.Needs.Length > 0 ? verdict.Needs + " access" : "the access it needs")} to this repository, so I did not take the risk. "
            + "Try again in a moment; if it keeps happening, the operator should check the factory's token.",
    };

    public const string NoticeMarker = "<!-- ai-sdlc-factory:notice -->";

    /// <summary>A comment arrived while the build was running, which no longer takes new input.</summary>
    public const string CommentTooLate =
        "I saw your comment, but the build has already started and it cannot take new input, so it was not used. " +
        "To change what is built, update the ticket text, then close and reopen the ticket; that starts a new run.";

    /// <summary>Comments that arrived but that no stage ever asked for.</summary>
    public static string CommentsNotUsed(int count) =>
        $"{count} comment{(count == 1 ? "" : "s")} arrived during this run, but no stage was waiting for an answer, so " +
        $"{(count == 1 ? "it was" : "they were")} not used. To have {(count == 1 ? "it" : "them")} taken into account, add " +
        "the details to the ticket text, then close and reopen the ticket.";

    /// <summary>
    /// Said when a build round starts, so a long silence has a reason: what the developer is working on. After a review the findings
    /// are listed (shortened); after a failed check the failure was already reported, so it only points back to it.
    /// </summary>
    public static string BuildRoundStarted(int round, int maxRounds, string? feedback)
    {
        if (feedback is null)
        {
            return $"**Build round {round} of {maxRounds}:** the developer is working on the ticket.";
        }
        var text = new StringBuilder();
        if (feedback.StartsWith(ReviewFeedback.Header, StringComparison.Ordinal))
        {
            var findings = feedback[ReviewFeedback.Header.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            text.AppendLine($"**Build round {round} of {maxRounds}:** the developer is fixing {findings.Length} finding{(findings.Length == 1 ? "" : "s")} from the review.");
            text.AppendLine();
            foreach (var finding in findings.Take(6))
            {
                text.AppendLine($"- {Inert(finding.Length > 240 ? finding[..240] + "..." : finding)}");
            }
            if (findings.Length > 6)
            {
                text.AppendLine($"- ... and {findings.Length - 6} more");
            }
            return text.ToString().TrimEnd();
        }
        return $"**Build round {round} of {maxRounds}:** the developer is fixing the problem reported above.";
    }

    /// <summary>What the developer says it did in a round; this is its own report, not a verified fact (the checks and reviews decide).</summary>
    public static string DeveloperFinished(int round, DeveloperReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"**Build round {round}: the developer finished**");
        text.AppendLine();
        var summary = Inert(report.Summary);
        text.AppendLine(summary.Length > 800 ? summary[..800] + "..." : summary);
        foreach (var note in report.Notes.Take(5))
        {
            var line = Inert(note);
            text.AppendLine($"- {(line.Length > 240 ? line[..240] + "..." : line)}");
        }
        if (report.Notes.Count > 5)
        {
            text.AppendLine($"- ... and {report.Notes.Count - 5} more");
        }
        text.AppendLine();
        text.Append("The checks and the review decide whether this is right.");
        return text.ToString();
    }

    /// <summary>Added to a failure caused by the factory's own machinery rather than by the ticket or the agent's work.</summary>
    public const string RetryHint =
        " This looks like a problem with the factory itself, not with your ticket. Comment `/retry` to run it again.";

    /// <summary>A comment on a feature ticket that has no running workflow.</summary>
    public const string NoRunActive =
        "No factory run is active for this ticket (it has finished, or was never started), so this comment was not used. " +
        "To run it again, comment `/retry` (or close and reopen the ticket); update the ticket text first if the requirements changed.";

    /// <summary>`/qa` on a ticket with no open pull request of the factory.</summary>
    public const string QaNoPullRequest =
        "I could not find an open pull request made by the factory for this ticket, so there is nothing to test. `/qa` works once the pull request is open.";

    public const string QaClosed = "This pull request is closed or merged, so I did not run QA on it.";

    public const string QaNotConfigured =
        "This project has no `verify` settings in `.factory/project.json`, so I do not know how to start the application or run end-to-end tests. " +
        "Add them on the default branch (see the project setup guide), merge that into the pull request's branch, then comment `/qa` again.";

    public static string QaSuiteMissing(string requested, IEnumerable<string> configured) =>
        $"This project has no `{requested}` suite in `verify.suites`. It has: {string.Join(", ", configured.Select(name => $"`{name}`"))}.";

    public static string QaBlocked(string summary, string mode = QaRequest.Focused) =>
        $"**QA: the tester could not write tests it could stand behind**\n\n{QaReportFormat.ScopeLine(mode)}\n\n{Inert(summary.Length > 1500 ? summary[..1500] + "..." : summary)}";

    /// <summary>Said before the tester runs: the change is all UI and the project has no <c>ui</c> suite, so the tests can only exercise the API. The run goes on.</summary>
    public static string QaUiGap(string gap, string mode) =>
        $"**QA: a heads up before I start**\n\n{QaReportFormat.ScopeLine(mode)}\n\n{gap} I carry on, because API tests can still be of use.";

    /// <summary>`/fix qa` when no QA report with findings can be found.</summary>
    public const string QaNothingToFix =
        "I could not find a QA report with findings to fix on this ticket or pull request. Run `/qa` first, or say what to change after `/fix`.";

    /// <summary>`/qa` while a QA run for the same ticket or pull request is still going.</summary>
    public const string QaBusy =
        "A QA run is already going for this one. Its report will be posted here when it is done.";

    public const string PullReviewClosed = "This pull request is closed or merged, so I did not review it.";

    public const string PullReviewBusy =
        "A review of this pull request is already going. Its findings will be posted here when it is done; comment `/review` again after that if the pull request has changed.";

    public const string PullReviewNoChanges =
        "I could not read what this pull request changes from the git host, so I did not review it. Comment `/review` again in a moment.";

    /// <summary>The comment that ends a <c>/review</c>: what each reviewer concluded, and that nothing was changed.</summary>
    public static string PullReviewDone(int pullNumber, IReadOnlyList<(string Role, ReviewReport Report)> reports, bool diffCut)
    {
        var text = new StringBuilder();
        text.AppendLine($"**Review of pull request #{pullNumber} finished**");
        text.AppendLine();
        foreach (var (role, report) in reports)
        {
            var blocking = report.Blocking.Count();
            text.AppendLine($"- {role}: {(blocking == 0 ? "no blocking findings" : $"{blocking} blocking finding{(blocking == 1 ? "" : "s")}")}{(report.Advice.Any() ? $", {report.Advice.Count()} other note{(report.Advice.Count() == 1 ? "" : "s")}" : "")}");
        }
        text.AppendLine();
        if (diffCut)
        {
            text.AppendLine("The diff was too long to read in full, so the reviewers saw only its start.");
            text.AppendLine();
        }
        text.Append("The findings are in the comments above. I read the diff only: the project's checks were not run, nothing was changed, and I do not merge. A person decides.");
        return text.ToString();
    }

    /// <summary>`/update` on a ticket with no open pull request of the factory.</summary>
    public const string UpdateNoPullRequest =
        "I could not find an open pull request made by the factory for this ticket, so there is nothing to update. `/update` works once the pull request is open.";

    public const string UpdateClosed = "This pull request is closed or merged, so I did not update it.";

    /// <summary>`/update` on a pull request the factory did not open.</summary>
    public const string UpdateNotFactorys =
        "`/update` works only on pull requests the factory opened itself (a `factory/issue-<n>` branch in this repository, authored by the factory's bot). This one is not, so I changed nothing.";

    /// <summary>`/fix` on a pull request the factory did not open.</summary>
    public const string RevisionNotFactorys =
        "`/fix` works only on pull requests the factory opened itself (a `factory/issue-<n>` branch in this repository, authored by the factory's bot). This one is not, so I changed nothing.";

    /// <summary>`/update` while an update for the same ticket or pull request is still going.</summary>
    public const string UpdateBusy =
        "An update of this pull request is already going. What it did will be posted here when it is done; comment `/update` again after that if the branch is still behind.";

    /// <summary>The run used its token budget, so it stopped before asking another agent.</summary>
    public static string TokenBudgetReached(long used, long budget) =>
        $"**Token budget reached**\n\nThis run has used {used:N0} tokens of its budget of {budget:N0} (input, output, reasoning and cache writes; cached input that was only read again is not counted), so I stopped before asking another agent. " +
        "To allow more, raise `limits.token_budget` in `.factory/project.json` (the operator's `FACTORY_MAX_TOKENS_PER_RUN` is the most a project can ask for), then comment `/retry` (a feature or bug run keeps the stages that finished).";

    private static readonly Dictionary<string, string> RunNames = new()
    {
        [WorkIds.Feature] = "a feature", [WorkIds.Bug] = "a bug fix", [WorkIds.Incident] = "an incident analysis", [WorkIds.Plan] = "a plan",
        [WorkIds.Docs] = "a documentation run", [WorkIds.Revision] = "a revision", [WorkIds.Qa] = "a `/qa` run", [WorkIds.Update] = "an `/update`",
    };

    /// <summary>The kinds of run that start with an estimate on the ticket (the cheap ones, such as a question or a review, do not).</summary>
    public static bool HasEstimate(string kind) => RunNames.ContainsKey(kind);

    /// <summary>"Rounded for reading": 483,211 is about 480,000; 28,400 is about 28,000.</summary>
    public static long Rounded(long tokens) => tokens >= 100_000 ? (tokens + 5_000) / 10_000 * 10_000 : (tokens + 500) / 1_000 * 1_000;

    /// <summary>What a run is likely to use, from the repository's earlier runs of its kind. It is a figure for a person to read; it never stops or delays a run.</summary>
    public static string Estimate(string kind, UsageEstimate estimate) =>
        estimate.Known
            ? $"**Estimate:** {Capital(RunNames[kind])} on this repository has typically used about {Rounded(estimate.Median):N0} tokens (the median of the last {estimate.Runs} runs). It is only a guide: nothing is stopped because of it."
            : $"**Estimate:** there is no history yet for {RunNames[kind]} on this repository (fewer than {UsageEstimates.MinimumRuns} earlier runs), so I cannot say what this run will cost.";

    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A run that ended without its result says what it cost, because a failed run is where tokens are wasted.</summary>
    public static string TokensOfStoppedRun(long tokens) =>
        $"This run used about {Rounded(tokens):N0} tokens before it stopped (input, output, reasoning and cache writes).";

    /// <summary>A <c>/qa</c> run is estimated over the project's confirmation threshold, so it waits for a person to say go.</summary>
    public static string QaWaitingForConfirmation(UsageEstimate estimate, long threshold) =>
        $"**This QA run needs your go-ahead**\n\nA `/qa` run on this repository has typically used about {Rounded(estimate.Median):N0} tokens (the median of the last {estimate.Runs} runs), more than the {threshold:N0} this project asks to confirm (`limits.confirm_over`). " +
        "Comment `/qa go` to start it. Close the ticket to cancel; I wait up to 7 days.";

    public const string QaConfirmed = "Confirmed. Starting the QA run.";

    public const string QaNotConfirmed = "Nobody confirmed the QA run within 7 days, so I did not start it. Comment `/qa` to ask again.";

    /// <summary><c>/qa go</c> when no run is waiting for it.</summary>
    public const string QaNothingToConfirm = "No QA run is waiting for a go-ahead on this ticket or pull request. Comment `/qa` to start one.";

    /// <summary>A daily token limit is reached, so the run waits until the next UTC day instead of failing.</summary>
    public static string DailyLimitWaiting(bool allRepositories, string repository, DateTimeOffset resume) =>
        $"**Daily token limit reached**\n\nThe factory has used its token limit for today ({(allRepositories ? "for all repositories" : $"for `{Inert(repository)}`")}), so I am waiting and will carry on after {resume:HH:mm} UTC on {resume:yyyy-MM-dd}. Close the ticket to stop the run.";

    /// <summary>An agent's run failed and is being run once more; the tokens of the failed run are spent either way.</summary>
    public static string AgentRunRepeated(string role, string problem) =>
        $"The {Inert(role)}'s run did not finish ({Inert(problem.Length > 160 ? problem[..160] + "..." : problem)}), so I am running it once more. The failed run's tokens are spent either way.";

    /// <summary>The model provider says its usage limit is reached; the run stopped instead of asking again.</summary>
    public static string ProviderLimitReached(string role, string said) =>
        $"**The model provider's usage limit is reached**\n\nThe {Inert(role)}'s run failed with: `{Inert(said.Length > 300 ? said[..300] : said)}`. Nothing is wrong with the ticket. I stopped instead of asking again; comment `/retry` once the limit has reset.";

    /// <summary>`.factory/project.json` has keys the factory does not know. The run goes ahead; the file is read without them.</summary>
    public static string UnknownProjectKeys(IReadOnlyList<string> warnings) =>
        "**`.factory/project.json`: " + (warnings.Count == 1 ? "a setting I do not know" : $"{warnings.Count} settings I do not know") + "**\n\n" +
        string.Join('\n', warnings.Select(warning => $"- {warning}")) +
        "\n\nThe run goes ahead with the file read without " + (warnings.Count == 1 ? "it" : "them") + ". A misspelled setting does nothing, so fix the name on the default branch if it was meant to count.";

    /// <summary>`.factory/instructions.md` is longer than the limit; the agents get the first part.</summary>
    public static string InstructionsCut(int bytes) =>
        $"**`.factory/instructions.md` is longer than {bytes / 1024} KB**\n\nThe agents get only its first {bytes / 1024} KB. Shorten the file on the default branch if the rest was meant to count.";

    /// <summary>`/retry` found nothing finished from an earlier run.</summary>
    public const string ResumeNothingSaved =
        "Nothing finished in an earlier run was saved for this ticket, so I start from the beginning.";

    /// <summary>`/retry` found saved results, but the ticket or the repository is no longer what they were made from.</summary>
    public static string ResumeNotUsable(string why) =>
        $"I start from the beginning, because {why}.";

    /// <summary>`/retry` carries on from what an earlier run finished.</summary>
    public static string ResumeUsing(IReadOnlyList<string> kept) =>
        $"Carrying on from the earlier run: I keep {string.Join(" and ", kept)} and do not ask for them again. Comment `/retry restart` to start from the beginning instead.";

    /// <summary>`/retry` on a ticket whose run is still going.</summary>
    public const string RetryWhileRunning =
        "A factory run is still active for this ticket, so `/retry` was not needed. It works once the run has finished.";

    /// <summary>`/retry` on a ticket that already has an open pull request: a new run would open a second one.</summary>
    public static string RetryHasPullRequest(int pullNumber) =>
        $"This ticket already has an open pull request (#{pullNumber}), so I did not start another run: it would open a second pull request. " +
        "To change that pull request, comment `/fix <what to change>` on it. To start over anyway, comment `/retry restart`.";

    /// <summary>A ticket was reopened, but its earlier run did not finish stopping in time, so no new run was started.</summary>
    public const string ReopenStillStopping =
        "This ticket was reopened, but its earlier run was still going and did not stop in time, so I did not start a new one. Comment `/retry restart` once the earlier run has finished.";

    /// <summary>Posted instead of the triage comment when a ticket runs in quick mode.</summary>
    public static string QuickMode(string reason, IReadOnlyList<string> skippedDesignReasons)
    {
        var text = new StringBuilder();
        text.AppendLine("**Quick mode**");
        text.AppendLine();
        text.AppendLine($"Because {reason}, I skip the coverage check, the design review and the acceptance check. The analyst, the developer, the project's checks and the reviewers still run.");
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
        NoMentions(value).Replace("\r", " ").Replace("\n", " ").Replace("`", "'").Trim();

    /// <summary>Puts a zero-width space after every <c>@</c>, so text from outside cannot notify a person. Line breaks and code formatting are kept.</summary>
    public static string NoMentions(string value) => value.Replace("@", "@\u200b");

    private static string YesNo(bool value) => value ? "yes" : "no";
}
