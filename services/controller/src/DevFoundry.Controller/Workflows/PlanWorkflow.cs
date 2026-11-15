using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

/// <summary>
/// One run per <c>plan</c> ticket. A planner reads the ticket and the repository and splits the request into feature tickets; nothing is
/// built and nothing is created until a person writes <c>/approve</c> (or <c>/approve 2 3</c> for chosen ones). The factory does not react
/// to merges yet, so a plain approval creates only the tickets that wait for nothing; a ticket that waits for another is created when a
/// person names it. Any other comment is a change to the plan and makes the planner plan again, until the first ticket is created.
/// The created tickets carry the <c>feature</c> label, so the feature workflow starts on each of them through the webhook.
/// </summary>
[Workflow("PlanWorkflow")]
public sealed class PlanWorkflow : FactoryWorkflowBase
{
    private const int MaxRefreshes = 3;

    /// <summary>The tickets already opened, by plan number. Kept in the run, so a second <c>/approve</c> never opens one twice.</summary>
    private readonly Dictionary<int, CreatedTicket> created = [];

    /// <summary>How many comments from people were waiting when the proposal was written, so that only those count as given while planning.</summary>
    private int repliesBeforeProposal;

    private int refreshes;
    private bool finalNoticePosted;

    [WorkflowSignal("human_reply")]
    public Task OnHumanReplyAsync(HumanReply reply) => AcceptHumanReplyAsync(reply);

    [WorkflowSignal("ticket_closed")]
    public Task OnTicketClosedAsync()
    {
        MarkTicketClosed();
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public Task<WorkResult> RunAsync(WorkRequest request) => RunPipelineAsync(request, "plan", StagesAsync);

    private async Task<WorkResult> StagesAsync(WorkContext work)
    {
        var (request, workspace, project) = work;
        var feedback = new List<string>();
        TicketPlan? previous = null;
        while (true)
        {
            var planned = await ConverseAsync<TicketPlan>(
                request, workspace, project.Image, "planner", maxClarificationRounds + 1,
                (round, answers, problem) => PlanPrompts.Planner(request, previous, feedback, answers, problem, round, maxClarificationRounds + 1),
                text => PlanParser.TryParse(text, out var value, out var error) ? (value, null) : (null, error),
                plan =>
                {
                    // Taken here, before the proposal is posted: a comment that arrives after this can be a reaction to it.
                    repliesBeforeProposal = replies.Count;
                    return PlanComments.Proposal(plan);
                },
                plan => plan.Verdict switch { TicketPlan.Pass => Next.Done, TicketPlan.Reject => Next.Stop, _ => Next.Ask },
                failedStatus: Statuses.PlanFailed, rejectedStatus: Statuses.Rejected, failureText: "The plan failed");
            if (planned.Stop is { } stop)
            {
                return stop;
            }
            var plan = planned.Verdict!;
            await DropApprovalsGivenWhilePlanningAsync(request);

            // A result ends the run; none means a person asked for changes, so plan again.
            if (await AnswerPeopleAsync(request, plan, feedback) is { } done)
            {
                return done;
            }
            previous = plan;
        }
    }

    /// <summary>
    /// An approval that arrived while the planner was still working was meant for an older plan, not the one just posted, and a person
    /// has not seen this one. It is dropped, with a notice, so no ticket is ever created from a plan nobody read. Only comments that were
    /// already waiting when the proposal was written count: one that arrives after that (a person approving as soon as the proposal appears)
    /// may well be a reaction to it, and can reach the workflow before this code runs.
    /// </summary>
    private async Task DropApprovalsGivenWhilePlanningAsync(WorkRequest request)
    {
        var dropped = 0;
        for (var index = Math.Min(repliesBeforeProposal, replies.Count) - 1; index >= 0; index--)
        {
            if (PlanCommands.Parse(replies[index].Body) is not null)
            {
                replies.RemoveAt(index);
                dropped++;
            }
        }
        if (dropped > 0)
        {
            await CommentAsync(request, PlanComments.ApprovalWhilePlanning);
        }
    }

    /// <summary>
    /// From here on people decide. Only this loop reads their comments, so a comment is never lost. <c>/approve</c> creates tickets; any
    /// other comment is a change to the plan, which returns null so the caller plans again. Once a ticket exists the plan is final.
    /// </summary>
    private async Task<WorkResult?> AnswerPeopleAsync(WorkRequest request, TicketPlan plan, List<string> feedback)
    {
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
                return new WorkResult(created.Count > 0 ? Statuses.PlanCreated : Statuses.TimedOut);
            }

            var comments = replies.ToList();
            replies.Clear();
            var numbers = new List<int>();
            var approver = "";
            var approved = false;
            var changes = new List<string>();
            var unclear = false;
            foreach (var reply in comments)
            {
                if (PlanCommands.Parse(reply.Body) is { } approval)
                {
                    approved = true;
                    approver = reply.Author;
                    numbers.AddRange(approval.Numbers);
                }
                else if (PlanCommands.IsOnlyAnUnclearApproval(reply.Body))
                {
                    unclear = true;
                }
                else
                {
                    changes.Add(reply.Body);
                }
            }
            if (unclear)
            {
                await CommentAsync(request, PlanComments.UnclearApproval);
            }

            // Asked to change the plan and to approve it in one go: the plan being approved is not the one asked for, so change it first.
            if (changes.Count > 0 && created.Count == 0)
            {
                if (++refreshes > MaxRefreshes)
                {
                    await CommentAsync(request, PlanComments.NothingToRefresh);
                    continue;
                }
                if (approved)
                {
                    await CommentAsync(request, "You asked for a change and approved in the same breath, so I created nothing. I will change the plan; approve the new one.");
                }
                feedback.AddRange(changes);
                return null;
            }

            if (changes.Count > 0 && !finalNoticePosted)
            {
                finalNoticePosted = true;
                await CommentAsync(request, PlanComments.PlanIsFinal);
            }
            if (approved && await CreateTicketsAsync(request, plan, numbers, approver))
            {
                return new WorkResult(Statuses.PlanCreated);
            }
        }
    }

    /// <summary>
    /// Opens the tickets a person approved: the ones named, or, with no number, every ticket that waits for nothing. Returns true when
    /// the whole plan has been created. A ticket that fails to open stops the batch, and what was already opened stays.
    /// </summary>
    private async Task<bool> CreateTicketsAsync(WorkRequest request, TicketPlan plan, IReadOnlyList<int> named, string approver)
    {
        var requested = (named.Count == 0 ? plan.Ready.ToList() : named.Distinct().ToList());
        var unknown = requested.Where(n => n < 1 || n > plan.Tickets.Count).ToList();
        if (unknown.Count > 0)
        {
            await CommentAsync(request, PlanComments.NoSuchTicket(unknown, plan.Tickets.Count));
        }
        var valid = requested.Except(unknown).ToList();
        var already = valid.Where(created.ContainsKey).ToList();
        if (already.Count > 0 && named.Count > 0)
        {
            await CommentAsync(request, PlanComments.AlreadyCreated(already));
        }
        var todo = valid.Except(already).OrderBy(n => n).ToList();
        if (todo.Count == 0)
        {
            if (unknown.Count == 0 && (named.Count == 0 || already.Count == 0))
            {
                await CommentAsync(request, PlanComments.NothingToApprove(created.Count == plan.Tickets.Count));
            }
            return created.Count == plan.Tickets.Count;
        }

        var opened = new List<CreatedTicket>();
        foreach (var number in todo)
        {
            var (title, body) = PlanComments.NewTicket(request, plan, number, created, approver);
            try
            {
                // One attempt: a retry after a lost answer would open the same ticket twice.
                var issue = await Workflow.ExecuteActivityAsync(
                    (ForgejoActivities activities) => activities.CreateIssueAsync(
                        new CreateIssueRequest(request.Owner, request.Repo, title, body, new List<string> { EventRouter.FeatureLabel })),
                    ActivityPolicy.Local(TimeSpan.FromSeconds(60), 1));
                var made = new CreatedTicket(number, issue.Number, issue.Url);
                created[number] = made;
                opened.Add(made);
            }
            catch (ActivityFailureException error)
            {
                await CommentAsync(request, PlanComments.NotCreated(TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))));
                break;
            }
        }
        if (opened.Count > 0)
        {
            await CommentAsync(request, PlanComments.Created(opened, plan, created));
        }
        return created.Count == plan.Tickets.Count;
    }
}
