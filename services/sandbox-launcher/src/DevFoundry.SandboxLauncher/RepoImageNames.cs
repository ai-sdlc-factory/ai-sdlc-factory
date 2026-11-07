using System.Text.RegularExpressions;

namespace DevFoundry.SandboxLauncher;

/// <summary>Names of images built from a repository: the alias a run uses and the Docker tag behind it.</summary>
public static partial class RepoImageNames
{
    public const string Repository = "dev-foundry/repo";
    public const string Label = "dev-foundry.repo-image";

    [GeneratedRegex(@"\Arepo-[0-9a-f]{12}\z")]
    private static partial Regex AliasPattern();

    public static bool IsAlias(string? alias) => alias is not null && AliasPattern().IsMatch(alias);
    public static string AliasFor(string hash) => $"repo-{hash}";
    public static string TagFor(string alias) => $"{Repository}:{alias["repo-".Length..]}";
}
