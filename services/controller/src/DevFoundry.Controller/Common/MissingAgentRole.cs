namespace DevFoundry.Controller;

/// <summary>
/// OpenCode does not fail when a role file is missing: it warns on stderr and answers as its default agent, without the role's
/// instructions or answer shape. That looks like an agent that keeps giving unusable answers, so it is recognised here and the
/// run stops at once with the real reason.
/// </summary>
public static class MissingAgentRole
{
    public static bool IsReported(string? stderr, string role) =>
        stderr is not null && stderr.Contains($"agent \"{role}\" not found", StringComparison.OrdinalIgnoreCase);

    public static string Describe(string role) =>
        $"the role file for \"{role}\" is missing from the agents folder the sandboxes get (FACTORY_GLOBAL_CONFIG), probably an out-of-date copy of agents/";
}
