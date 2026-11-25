namespace AiSdlcFactory.Controller;

/// <summary>Things worth saying once at start about how a git host is set up, because they are easy to forget until they matter.</summary>
public static class HostStartupNotes
{
    /// <summary>
    /// A GitHub host whose default rule holds nobody back: on a public repository any GitHub account can then use the slash commands, answer an agent and ask for a revision, and so spend
    /// the factory's model login and put its own text in front of an agent (nothing merges without a person). Forgejo is left out: the bundled instance requires sign-in and has registration off.
    /// A repository the operator gave a rule of its own is not looked at here.
    /// </summary>
    public static string? OpenCommandsOnGitHub(HostSettings host)
    {
        if (host.Type != GitHubWebhookAdapter.TypeName || (host.Commands ?? HostCommandRules.Open).Default.HoldsBack)
        {
            return null;
        }
        return $"Git host {host.Name} ({host.Type}) has no rule on who may use commands, so on a public repository any GitHub account can use /fix, /qa, /ask and the other commands and spend the model login. " +
               "Consider \"commands\":{\"who\":\"write\"} in this host's entry of FACTORY_HOSTS (docs/git-hosts.md).";
    }
}
