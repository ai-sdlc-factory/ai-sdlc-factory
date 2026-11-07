using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

/// <summary>
/// One run per incident ticket, advice only: nothing here touches production, and the ticket (written by a person or
/// by a separate collector) is the only source of facts. An incident analyst reads it, an investigator looks for causes
/// in the code and its git history (the factory checks the files and commits it cites), a mitigator proposes options,
/// and then people drive: <c>/option N</c> for a checklist, <c>/hotfix</c> to open a bug ticket, <c>/resolved</c> to get
/// a draft postmortem as a pull request. Any other comment is new information and starts another look.
/// </summary>
[Workflow("IncidentWorkflow")]
public sealed partial class IncidentWorkflow : FactoryWorkflowBase
{
    private const int MaxRefreshes = 3;

    /// <summary>What happened on the ticket, in order, for the postmortem.</summary>
    private readonly List<string> timeline = [];

    [WorkflowSignal("human_reply")]
    public Task OnHumanReplyAsync(HumanReply reply)
    {
        Note(reply.Author, reply.Body);
        return AcceptHumanReplyAsync(reply);
    }

    [WorkflowSignal("ticket_closed")]
    public Task OnTicketClosedAsync()
    {
        MarkTicketClosed();
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public Task<WorkResult> RunAsync(WorkRequest request) => RunPipelineAsync(request, "incident", StagesAsync);

    private void Note(string who, string text) =>
        timeline.Add($"{Workflow.UtcNow:HH:mm} UTC {(string.IsNullOrWhiteSpace(who) ? "someone" : who)}: {PromptText.Clip(text, 300).Replace('\n', ' ')}");

    private async Task<WorkResult> StagesAsync(WorkContext work)
    {
        var (request, workspace, project) = work;
        var assessed = await ConverseAsync<IncidentAssessment>(
            request, workspace, project.Image, "incident-analyst", maxClarificationRounds + 1,
            (round, answers, problem) => IncidentPrompts.Assessor(request, answers, problem, round, maxClarificationRounds + 1),
            text => IncidentParser.TryParseAssessment(text, out var value, out var error) ? (value, null) : (null, error),
            IncidentComments.Assessment,
            value => value.Verdict switch { IncidentAssessment.Pass => Next.Done, IncidentAssessment.Reject => Next.Stop, _ => Next.Ask },
            failedStatus: Statuses.AssessmentFailed, rejectedStatus: Statuses.Rejected, failureText: "The incident assessment failed");
        if (assessed.Stop is { } stop)
        {
            return stop;
        }
        var assessment = assessed.Verdict!;
        Note("factory", $"Assessed as {assessment.Severity}: {assessment.Summary}");

        var more = new List<string>();
        while (true)
        {
            var investigated = await InvestigateAsync(work, assessment, more);
            if (investigated.Stop is { } investigationStop)
            {
                return investigationStop;
            }
            var investigation = investigated.Verdict!;

            var proposed = await ConverseAsync<Mitigation>(
                request, workspace, project.Image, "mitigator", Rounds(MaxDesignRounds),
                (round, answers, problem) => IncidentPrompts.Mitigator(request, assessment, investigation, more, answers, problem, round, Rounds(MaxDesignRounds)),
                text => IncidentParser.TryParseMitigation(text, out var value, out var error) ? (value, null) : (null, error),
                IncidentComments.Options,
                value => value.Verdict == Mitigation.Pass ? Next.Done : Next.Ask,
                failedStatus: Statuses.MitigationFailed, rejectedStatus: Statuses.MitigationFailed, failureText: "The mitigation options failed");
            if (proposed.Stop is { } proposalStop)
            {
                return proposalStop;
            }
            var mitigation = proposed.Verdict!;
            Note("factory", $"Proposed {mitigation.Options.Count} option(s); recommended option {mitigation.Recommended}.");

            // A result ends the run; none means a person gave new information, so look again.
            if (await AnswerPeopleAsync(work, assessment, investigation, mitigation, more) is { } done)
            {
                return done;
            }
        }
    }

    private int refreshes;
    private int hotfixTicket;

    /// <summary>
    /// From here on people decide. Only this loop reads their comments, so a comment is never lost. Commands are answered
    /// at once (<c>/option N</c>, <c>/hotfix</c>); <c>/resolved</c> ends the run with a postmortem; any other comment is
    /// new information, added to <paramref name="more"/>, and returns null so the caller investigates again.
    /// </summary>
    private async Task<WorkResult?> AnswerPeopleAsync(
        WorkContext work, IncidentAssessment assessment, Investigation investigation, Mitigation mitigation, List<string> more)
    {
        var request = work.Request;
        while (true)
        {
            var answered = await Workflow.WaitConditionAsync(() => ticketClosed || replies.Count > 0, WaitForHuman);
            if (ticketClosed)
            {
                return new WorkResult(Statuses.Closed);
            }
            if (!answered)
            {
                await CommentAsync(request, "No reply for 7 days, so I am stopping. Close and reopen the ticket to start again.");
                return new WorkResult(Statuses.TimedOut);
            }

            var comments = replies.ToList();
            replies.Clear();
            var newInformation = false;
            var resolved = false;
            foreach (var reply in comments)
            {
                var commands = IncidentCommands.Parse(reply.Body);
                if (commands.Count == 0)
                {
                    more.Add(reply.Body);
                    newInformation = true;
                    continue;
                }
                foreach (var command in commands)
                {
                    switch (command)
                    {
                        case IncidentCommand.Option option when option.Number >= 1 && option.Number <= mitigation.Options.Count:
                            Note("factory", $"Checklist given for option {option.Number}.");
                            await CommentAsync(request, IncidentComments.Runbook(option.Number, mitigation.Options[option.Number - 1]));
                            break;
                        case IncidentCommand.Option option:
                            await CommentAsync(request, IncidentComments.NoSuchOption(option.Number, mitigation.Options.Count));
                            break;
                        case IncidentCommand.Hotfix:
                            hotfixTicket = await OpenHotfixAsync(request, assessment, investigation, hotfixTicket);
                            break;
                        case IncidentCommand.Resolved:
                            resolved = true;
                            break;
                    }
                }
            }

            if (resolved)
            {
                Note("factory", "Marked as resolved by a person.");
                return await PostmortemAsync(work, assessment, investigation, mitigation);
            }
            if (newInformation)
            {
                if (++refreshes > MaxRefreshes)
                {
                    await CommentAsync(request, IncidentComments.NothingToRefresh);
                    continue;
                }
                Note("factory", "New information received; looking again.");
                return null;
            }
        }
    }

    /// <summary>
    /// The investigator names hypotheses; the factory then checks the files and commits they cite and drops what it cannot
    /// find. A hypothesis with nothing left to point at is shown as a guess (low confidence).
    /// </summary>
    private async Task<StageResult<Investigation>> InvestigateAsync(WorkContext work, IncidentAssessment assessment, IReadOnlyList<string> more)
    {
        var (request, workspace, project) = work;
        var found = await ConverseAsync<Investigation>(
            request, workspace, project.Image, "investigator", Rounds(MaxDesignRounds),
            (round, answers, problem) => IncidentPrompts.Investigator(request, assessment, more, answers, problem, round, Rounds(MaxDesignRounds)),
            text => IncidentParser.TryParseInvestigation(text, out var value, out var error) ? (value, null) : (null, error),
            // Posted below, after the factory has checked what the investigator cited; only questions are posted early.
            value => value.Verdict == Investigation.Pass ? "" : IncidentComments.Investigation(value),
            value => value.Verdict == Investigation.Pass ? Next.Done : Next.Ask,
            failedStatus: Statuses.InvestigationFailed, rejectedStatus: Statuses.InvestigationFailed, failureText: "The investigation failed");
        if (found.Stop is not null)
        {
            return found;
        }

        var claimed = found.Verdict!;
        var files = await FindExistingFilesAsync(workspace, project.Image, InvestigationCheck.PathsToCheck(claimed));
        var commits = await FindCommitsAsync(workspace, project.Image, InvestigationCheck.CommitsToCheck(claimed));
        var verified = InvestigationCheck.Apply(claimed, files, commits);
        await CommentAsync(request, IncidentComments.Investigation(verified));
        Note("factory", $"Investigated: {verified.Hypotheses.Count} hypothesis(es). {verified.Summary}");
        return new StageResult<Investigation>(null, verified);
    }

    /// <summary>Which of the cited commits exist in the repository. If the check cannot run, nothing counts as verified.</summary>
    private static async Task<HashSet<string>> FindCommitsAsync(WorkspaceInfo workspace, string image, IReadOnlyList<string> commits)
    {
        if (commits.Count == 0)
        {
            return [];
        }
        var result = await RunCommandAsync(image, workspace, InvestigationCheck.CommitCommand(commits), 60, SandboxNetworks.None, false, "evidence");
        return result is { ExitCode: 0 } ? EvidenceCheck.ParseFound(result.Stdout) : [];
    }

    /// <summary>Opens one bug ticket, at a person's request, that carries what is known so the bug workflow can start from it.</summary>
    private async Task<int> OpenHotfixAsync(WorkRequest request, IncidentAssessment assessment, Investigation investigation, int alreadyOpened)
    {
        if (alreadyOpened > 0)
        {
            await CommentAsync(request, IncidentComments.HotfixAlreadyOpened);
            return alreadyOpened;
        }

        var top = investigation.Hypotheses.OrderByDescending(h => Array.IndexOf(IncidentParser.Levels, h.Confidence)).First();
        var body = new System.Text.StringBuilder();
        body.AppendLine($"Opened by the factory from incident #{request.IssueNumber}, at a person's request.");
        body.AppendLine();
        body.AppendLine("## What happens");
        body.AppendLine(TicketComments.Inert(assessment.Summary));
        body.AppendLine();
        body.AppendLine($"Impact: {TicketComments.Inert(assessment.Impact)}");
        body.AppendLine();
        body.AppendLine("## Suspected cause (a hypothesis from the incident investigation, so check it before relying on it)");
        body.AppendLine(TicketComments.Inert(top.Cause));
        if (top.Evidence.Count > 0)
        {
            body.AppendLine($"Files: {string.Join(", ", top.Evidence.Select(path => $"`{TicketComments.Inert(path)}`"))}");
        }
        if (top.Commits.Count > 0)
        {
            body.AppendLine($"Commits: {string.Join(", ", top.Commits.Select(commit => $"`{TicketComments.Inert(commit)}`"))}");
        }
        body.AppendLine();
        body.AppendLine("## What is wanted");
        body.AppendLine("A small, safe code change that stops the problem above, proven by a test that fails before the fix and passes after it.");

        var title = TicketComments.Inert($"Fix for incident #{request.IssueNumber}: {request.Title}");
        if (title.Length > 120)
        {
            title = title[..120];
        }
        var create = new CreateIssueRequest(request.Owner, request.Repo, title, body.ToString().TrimEnd(), new List<string> { EventRouter.BugLabel });
        try
        {
            var issue = await Workflow.ExecuteActivityAsync(
                (ForgejoActivities activities) => activities.CreateIssueAsync(create),
                ActivityPolicy.Local(TimeSpan.FromSeconds(60), 3));
            Note("factory", $"Opened bug ticket #{issue.Number}.");
            await CommentAsync(request, IncidentComments.HotfixOpened(issue.Number, issue.Url));
            return issue.Number;
        }
        catch (ActivityFailureException error)
        {
            await CommentAsync(request, $"I could not open the bug ticket: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))}");
            return 0;
        }
    }
}
