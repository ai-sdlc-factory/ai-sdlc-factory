
namespace DevFoundry.Controller;

/// <summary>Files an agent may never change: the factory's own configuration and the pipelines that run code.</summary>
public static class ProtectedPaths
{
    private static readonly string[] Prefixes = [".factory/", ".forgejo/", ".github/", ".gitea/", ".git/"];
    private static readonly string[] Files = ["bitbucket-pipelines.yml", ".gitlab-ci.yml", "jenkinsfile"];

    public static bool IsProtected(string path)
    {
        var lower = path.ToLowerInvariant().TrimStart('/');
        return Prefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal))
            || Files.Contains(lower, StringComparer.Ordinal);
    }
}
