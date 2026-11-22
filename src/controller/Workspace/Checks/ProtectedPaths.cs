namespace AiSdlcFactory.Controller;

/// <summary>Files an agent may never change: the factory's own configuration and the pipelines that run code, plus any a project adds.</summary>
public static class ProtectedPaths
{
    private static readonly string[] Prefixes = [".factory/", ".forgejo/", ".github/", ".gitea/", ".git/"];
    private static readonly string[] Files = ["bitbucket-pipelines.yml", ".gitlab-ci.yml", "jenkinsfile"];

    /// <summary>
    /// The built-in list always applies. <paramref name="extra"/> are patterns from the project (<c>protected_paths</c>): they can only
    /// add to the list, never take anything off it. Matching ignores case.
    /// </summary>
    public static bool IsProtected(string path, IReadOnlyList<string>? extra = null)
    {
        var lower = path.ToLowerInvariant().TrimStart('/');
        return Prefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal))
            || Files.Contains(lower, StringComparer.Ordinal)
            || (extra is { Count: > 0 } && extra.Any(pattern => Globs.IsMatch(lower, pattern.ToLowerInvariant())));
    }
}
