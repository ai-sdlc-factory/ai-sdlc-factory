namespace DevFoundry.Controller;

/// <summary>What the factory uses when a project does not say: set by the operator (see <c>FACTORY_DEFAULT_*</c>), and read through an activity so a run keeps the values it started with.</summary>
/// <param name="MaxTokensPerRun">The most tokens one run may use (see <see cref="TokenUsage.Counted"/>); a project can set a lower number with <c>limits.token_budget</c>, never a higher one. 0 means no limit.</param>
/// <param name="DailyTokenLimit">The most tokens all runs together may use in one UTC day, as the host's usage log counts them; a run that reaches it waits for the next day. 0 means no limit.</param>
/// <param name="RepoDailyTokenLimit">The same for one repository. 0 means no limit.</param>
public sealed record FactoryDefaults(
    int BuildRounds, int ClarificationRounds, int RevisionsPerPullRequest, bool AllowRepoImages = false,
    long MaxTokensPerRun = 0, long DailyTokenLimit = 0, long RepoDailyTokenLimit = 0)
{
    public static readonly FactoryDefaults Standard = new(3, 3, 3);
}

/// <summary>The limits a project may set in <c>limits</c>. A missing value means the factory's default.</summary>
public sealed record ProjectLimits(int? BuildRounds = null, int? ClarificationRounds = null, int? RevisionsPerPullRequest = null, int? TokenBudget = null)
{
    public EffectiveLimits Resolve(FactoryDefaults defaults) => new(
        BuildRounds ?? defaults.BuildRounds,
        ClarificationRounds ?? defaults.ClarificationRounds,
        RevisionsPerPullRequest ?? defaults.RevisionsPerPullRequest,
        EffectiveTokenBudget(TokenBudget, defaults.MaxTokensPerRun));

    /// <summary>The budget of one run: what the project asks for, but never more than the operator's maximum. 0 means no limit (neither is set).</summary>
    public static long EffectiveTokenBudget(long? project, long factoryMaximum) =>
        (project, factoryMaximum) switch
        {
            (null, _) => factoryMaximum,
            (var asked, 0) => asked.Value,
            (var asked, var maximum) => Math.Min(asked.Value, maximum),
        };
}

/// <param name="TokenBudget">Tokens one run may use before it stops, or 0 for no limit.</param>
public sealed record EffectiveLimits(int BuildRounds, int ClarificationRounds, int RevisionsPerPullRequest, long TokenBudget = 0);

/// <summary>The ranges that hold for both the factory's defaults and a project's settings. They are fixed: a project cannot raise a ceiling.</summary>
public static class LimitRanges
{
    public const int MinBuildRounds = 1;
    public const int MaxBuildRounds = 5;
    public const int MinClarificationRounds = 0;
    public const int MaxClarificationRounds = 4;
    public const int MinRevisions = 1;
    public const int MaxRevisions = 10;

    /// <summary>A budget below this would stop a run before its first agent has finished; above the maximum it is no limit worth setting.</summary>
    public const int MinTokenBudget = 10_000;
    public const int MaxTokenBudget = 1_000_000_000;

    public static bool BuildRoundsOk(int value) => value is >= MinBuildRounds and <= MaxBuildRounds;
    public static bool ClarificationRoundsOk(int value) => value is >= MinClarificationRounds and <= MaxClarificationRounds;
    public static bool RevisionsOk(int value) => value is >= MinRevisions and <= MaxRevisions;
    public static bool TokenBudgetOk(long value) => value is >= MinTokenBudget and <= MaxTokenBudget;
}
