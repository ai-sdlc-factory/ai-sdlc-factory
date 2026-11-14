using Temporalio.Workflows;

namespace DevFoundry.Controller;

public abstract partial class FactoryWorkflowBase
{
    /// <summary>The most tokens (<see cref="TokenUsage.Counted"/>) this run may use; 0 means no limit. The project's <c>limits.token_budget</c>, never above the operator's maximum.</summary>
    private long tokenBudget;

    /// <summary>The operator's limits on a UTC day, as the host's usage log counts them; 0 means no limit.</summary>
    private long dailyTokenLimit, repoDailyTokenLimit;

    // The day the run last looked at the daily limits, and how many tokens it had used by then: only what it uses after midnight counts against the new day.
    private DateOnly? limitDay;
    private long tokensAtDayStart;

    /// <summary>What this run's agents have used so far, as it counts against a budget or a daily limit.</summary>
    protected long TokensCounted => usages.Sum(usage => usage.Counted);

    /// <summary>Reads the limits for this run once the project's settings and the factory's defaults are known.</summary>
    private void SetTokenLimits(EffectiveLimits limits, FactoryDefaults defaults)
    {
        tokenBudget = limits.TokenBudget;
        dailyTokenLimit = defaults.DailyTokenLimit;
        repoDailyTokenLimit = defaults.RepoDailyTokenLimit;
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
