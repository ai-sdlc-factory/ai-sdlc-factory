namespace AiSdlcFactory.Controller;

/// <summary>
/// What a model run used, summed over the steps OpenCode reported. <see cref="Cost"/> is exactly what OpenCode
/// reports (0 with a ChatGPT login, which has no per-token price). <see cref="Partial"/> is true when the output
/// was cut and early steps may be missing, so the numbers are a lower bound.
/// </summary>
public sealed record TokenUsage(
    long Input, long Output, long Reasoning, long CacheRead, long CacheWrite, long Total, double Cost, int Steps, bool Partial = false)
{
    /// <summary>
    /// What counts against a token budget or a daily limit: input, output, reasoning and cache writes. Reads of cached input are left out, because they
    /// are the cheap part (the same text read again) and the total with them says little about the spend.
    /// </summary>
    public long Counted => Input + Output + Reasoning + CacheWrite;

    public static TokenUsage Sum(IEnumerable<TokenUsage> usages)
    {
        var list = usages.ToList();
        return new TokenUsage(
            list.Sum(u => u.Input), list.Sum(u => u.Output), list.Sum(u => u.Reasoning), list.Sum(u => u.CacheRead),
            list.Sum(u => u.CacheWrite), list.Sum(u => u.Total), list.Sum(u => u.Cost), list.Sum(u => u.Steps),
            list.Any(u => u.Partial));
    }
}
