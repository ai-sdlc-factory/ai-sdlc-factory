using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AiSdlcFactory.Controller;

public abstract partial class FactoryWorkflowBase
{
    /// <summary>The most tokens (<see cref="TokenUsage.Counted"/>) this run may use; 0 means no limit. The project's <c>limits.token_budget</c>, never above the operator's maximum.</summary>
    private long tokenBudget;

    /// <summary>The operator's limits on a UTC day, as the host's usage log counts them; 0 means no limit.</summary>
    private long dailyTokenLimit, repoDailyTokenLimit;

    // The day the run last looked at the daily limits, and how many tokens it had used by then: only what it uses after midnight counts against the new day.
    private DateOnly? limitDay;
    private long tokensAtDayStart;

    /// <summary>The project's (or the operator's) threshold for asking a person before a <c>/qa</c> run; 0 means never ask.</summary>
    private long confirmOver;

    /// <summary>What a run of this kind has typically used on this repository, once the run has looked it up (null for kinds that have no estimate, or when the log could not be read).</summary>
    protected UsageEstimate? runEstimate;

    /// <summary>A person commented <c>/qa go</c> (set by the QA workflow's signal).</summary>
    protected bool qaConfirmed;

    /// <summary>What this run's agents have used so far, as it counts against a budget or a daily limit.</summary>
    protected long TokensCounted => usages.Sum(usage => usage.Counted);

    /// <summary>Reads the limits for this run once the project's settings and the factory's defaults are known.</summary>
    private void SetTokenLimits(EffectiveLimits limits, FactoryDefaults defaults)
    {
        tokenBudget = limits.TokenBudget;
        dailyTokenLimit = defaults.DailyTokenLimit;
        repoDailyTokenLimit = defaults.RepoDailyTokenLimit;
        confirmOver = limits.ConfirmOver;
    }

    /// <summary>
    /// Says on the ticket what a run of this kind has typically used on this repository (from the usage log, which holds only numbers), so the cost is known before it is spent.
    /// Best effort and never a reason to refuse a run: if the log cannot be read, the run goes ahead without a figure.
    /// </summary>
    private async Task SayEstimateAsync(WorkRequest request)
    {
        if (!TicketComments.HasEstimate(request.Kind))
        {
            return;
        }
        try
        {
            runEstimate = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.EstimateUsageAsync($"{request.Owner}/{request.Repo}", request.Kind),
                ActivityPolicy.Local(TimeSpan.FromSeconds(30), 2));
        }
        catch (ActivityFailureException)
        {
            return;
        }
        await CommentAsync(request, TicketComments.Estimate(request.Kind, runEstimate));
    }

    /// <summary>
    /// For a <c>/qa</c> run whose estimate is over <c>limits.confirm_over</c>: waits for a person to comment <c>/qa go</c> before any tokens are spent. The run is already a person's explicit
    /// command, so asking again is their choice to have turned on; closing the ticket or seven days without an answer ends the wait. Returns the result to stop with, or null to go on.
    /// </summary>
    protected async Task<WorkResult?> ConfirmExpensiveRunAsync(WorkRequest request)
    {
        if (confirmOver <= 0 || runEstimate is not { Known: true } estimate || estimate.Median <= confirmOver)
        {
            return null;
        }
        await CommentAsync(request, TicketComments.QaWaitingForConfirmation(estimate, confirmOver));
        var answered = await Workflow.WaitConditionAsync(() => qaConfirmed || ticketClosed, WaitForHuman);
        if (ticketClosed)
        {
            return new WorkResult(Statuses.Closed);
        }
        if (!answered)
        {
            await CommentAsync(request, TicketComments.QaNotConfirmed);
            return new WorkResult(Statuses.NotConfirmed);
        }
        await CommentAsync(request, TicketComments.QaConfirmed);
        return null;
    }

    /// <summary>The endings where tokens were spent and no result came of it. Only these say what the run cost: a successful run's report stays as it is.</summary>
    public static bool IsStoppedStatus(string status) =>
        status.EndsWith("_failed", StringComparison.Ordinal)
        || status is Statuses.BuildBlocked or Statuses.Error or Statuses.UsageLimit or Statuses.CannotReproduce or Statuses.QaBlocked or Statuses.UpdateBlocked or Statuses.TimedOut;

    /// <summary>A run that failed or was stopped says what it used, because that is where tokens are wasted. Not said when nothing was spent (or when the budget message already said it).</summary>
    private async Task SayTokensOfStoppedRunAsync(WorkRequest request, WorkResult result)
    {
        if (IsStoppedStatus(result.Status) && TokensCounted > 0)
        {
            await CommentAsync(request, TicketComments.TokensOfStoppedRun(TokensCounted));
        }
    }

    /// <summary>
    /// Checked before every agent run, so an agent that has started is never cut off: a run that has used its budget stops, saving what it wrote, and a run that
    /// would go over a daily limit waits for the next UTC day instead of failing. A single agent run can still overshoot, which the agent's own timeout bounds.
    /// </summary>
    private async Task EnforceTokenLimitsAsync()
    {
        if (tokenBudget > 0 && TokensCounted >= tokenBudget)
        {
            throw new RunStopException(Statuses.BudgetExceeded, TicketComments.TokenBudgetReached(TokensCounted, tokenBudget));
        }
        if ((dailyTokenLimit > 0 || repoDailyTokenLimit > 0) && current is { } request)
        {
            await WaitForDailyRoomAsync(request);
        }
    }

    private async Task WaitForDailyRoomAsync(WorkRequest request)
    {
        var repository = $"{request.Owner}/{request.Repo}";
        var noticePosted = false;
        while (true)
        {
            var now = Workflow.UtcNow;
            var today = DateOnly.FromDateTime(now);
            if (limitDay != today)
            {
                limitDay = today;
                tokensAtDayStart = TokensCounted;
            }
            var logged = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.UsageTodayAsync(repository),
                ActivityPolicy.Local(TimeSpan.FromMinutes(1), 2));
            // The log has the runs that finished today; this run is not in it yet, so what it used today is added.
            var own = TokensCounted - tokensAtDayStart;
            var overAll = dailyTokenLimit > 0 && logged.All + own >= dailyTokenLimit;
            var overRepository = repoDailyTokenLimit > 0 && logged.Repository + own >= repoDailyTokenLimit;
            if (!overAll && !overRepository)
            {
                return;
            }

            var resume = new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            if (!noticePosted)
            {
                noticePosted = true;
                await CommentAsync(request, TicketComments.DailyLimitWaiting(overAll, repository, resume));
            }
            if (await Workflow.WaitConditionAsync(() => ticketClosed, resume - now + TimeSpan.FromMinutes(1)))
            {
                throw new RunStopException(Statuses.Closed, null);
            }
        }
    }
}
