namespace AiSdlcFactory.Controller;

/// <summary>How the agent runs of one role went in one run: the attempts made, how many answers were rejected (unusable, or against a rule), how many runs failed outright, and the tokens used.</summary>
public sealed record RoleStats(int Attempts, int Rejected, int Failed, long Tokens);

/// <summary>What a finished run adds to the run log besides its token total, for the weekly report on what repeats. Numbers only: nothing the ticket said.</summary>
/// <param name="Rounds">The most rounds a loop of the run reached (build, revision, update or documentation rounds); 0 when it had none.</param>
/// <param name="GateFailures">How many times one of the project's checks failed.</param>
public sealed record RunStats(int Rounds, int GateFailures, IReadOnlyDictionary<string, RoleStats> Roles);

public abstract partial class FactoryWorkflowBase
{
    private readonly Dictionary<string, RoleStats> roleStats = [];
    private int roundsUsed;
    private int gateFailures;

    /// <summary>A loop of the run reached this round.</summary>
    protected void NoteRound(int round) => roundsUsed = Math.Max(roundsUsed, round);

    /// <summary>One attempt of an agent: <paramref name="outcome"/> is "accepted", "rejected" or "failed".</summary>
    private void NoteAttempt(string role, string outcome, TokenUsage? spent)
    {
        var before = roleStats.GetValueOrDefault(role) ?? new RoleStats(0, 0, 0, 0);
        roleStats[role] = new RoleStats(
            before.Attempts + 1,
            before.Rejected + (outcome == "rejected" ? 1 : 0),
            before.Failed + (outcome == "failed" ? 1 : 0),
            before.Tokens + (spent?.Counted ?? 0));
    }

    /// <summary>What this run has done so far, for the run log.</summary>
    protected RunStats Stats => new(roundsUsed, gateFailures, new Dictionary<string, RoleStats>(roleStats));
}
