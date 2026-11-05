namespace DevFoundry.Controller;

/// <summary>What the factory uses when a project does not say: set by the operator (see <c>FACTORY_DEFAULT_*</c>), and read through an activity so a run keeps the values it started with.</summary>
public sealed record FactoryDefaults(int BuildRounds, int ClarificationRounds, int RevisionsPerPullRequest)
{
    public static readonly FactoryDefaults Standard = new(3, 3, 3);
}

/// <summary>The limits a project may set in <c>limits</c>. A missing value means the factory's default.</summary>
public sealed record ProjectLimits(int? BuildRounds = null, int? ClarificationRounds = null, int? RevisionsPerPullRequest = null)
{
    public EffectiveLimits Resolve(FactoryDefaults defaults) => new(
        BuildRounds ?? defaults.BuildRounds,
        ClarificationRounds ?? defaults.ClarificationRounds,
        RevisionsPerPullRequest ?? defaults.RevisionsPerPullRequest);
}

public sealed record EffectiveLimits(int BuildRounds, int ClarificationRounds, int RevisionsPerPullRequest);

/// <summary>The ranges that hold for both the factory's defaults and a project's settings. They are fixed: a project cannot raise a ceiling.</summary>
public static class LimitRanges
{
    public const int MinBuildRounds = 1;
    public const int MaxBuildRounds = 5;
    public const int MinClarificationRounds = 0;
    public const int MaxClarificationRounds = 4;
    public const int MinRevisions = 1;
    public const int MaxRevisions = 10;

    public static bool BuildRoundsOk(int value) => value is >= MinBuildRounds and <= MaxBuildRounds;
    public static bool ClarificationRoundsOk(int value) => value is >= MinClarificationRounds and <= MaxClarificationRounds;
    public static bool RevisionsOk(int value) => value is >= MinRevisions and <= MaxRevisions;
}
