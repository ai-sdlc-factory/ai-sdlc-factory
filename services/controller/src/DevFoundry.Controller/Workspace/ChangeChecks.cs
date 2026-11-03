using System.Text;
using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

public sealed record FileChange(string Path, string Kind)
{
    public const string Added = "added";
    public const string Modified = "modified";
    public const string Deleted = "deleted";
}

/// <summary>What the agent changed in its workspace, and everything that stops those changes from being published.</summary>
public sealed record ChangeReport(IReadOnlyList<FileChange> Files, IReadOnlyList<string> Violations);

public sealed record PublishRequest(string Name, string Message, RunRecordSpec? Record = null);

public sealed record PublishResult(string CommitSha, string Branch, int FileCount);

public sealed record RescueRequest(string Name, string Message, RunRecordSpec? Record = null);

/// <summary>
/// What a rescue did with a run that could not finish: whether the work so far was saved to a branch, what was
/// left out and why, and where the full record of the run can be read.
/// </summary>
public sealed record RescueResult(
    bool Saved, string? Branch, int FileCount, IReadOnlyList<string> Excluded, string Note, string TraceUrl);

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

/// <summary>
/// A deterministic scan of changed file contents for credentials. It is a safety net, not a guarantee:
/// it catches common token shapes and the factory's own token, and reports where, never the value.
/// </summary>
public static partial class SecretScan
{
    private const int MaxScanBytes = 1024 * 1024;

    private static readonly (string Rule, Regex Pattern)[] Rules =
    [
        ("private key", PrivateKey()),
        ("AWS access key", AwsKey()),
        ("GitHub token", GitHubToken()),
        ("Slack token", SlackToken()),
        ("hard-coded credential", Assignment()),
    ];

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}")]
    private static partial Regex SlackToken();

    // password = "something long" / api_key: 'abcdef...' with a quoted literal of 12+ non-space characters.
    [GeneratedRegex(@"(?i)\b(?:api[_-]?key|secret|token|passwd|password|private[_-]?key)\b\s*[:=]\s*[""'][^""'\s]{12,}[""']")]
    private static partial Regex Assignment();

    /// <summary>
    /// The text with every line that looks like a credential (or holds one of the known secrets) replaced by a
    /// marker. Used before agent traffic is written to a file that may be committed.
    /// </summary>
    public static string Redact(string text, IEnumerable<string> knownSecrets)
    {
        var secrets = knownSecrets.Where(secret => secret.Length >= 8).ToList();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (Rules.Any(rule => rule.Pattern.IsMatch(lines[i])) || secrets.Any(secret => lines[i].Contains(secret, StringComparison.Ordinal)))
            {
                lines[i] = "[line left out: possible secret]";
            }
        }
        return string.Join('\n', lines);
    }

    public static IEnumerable<string> Scan(string path, byte[] content, IEnumerable<string> knownSecrets)
    {
        if (content.Length > MaxScanBytes)
        {
            yield return $"{path} is larger than {MaxScanBytes / 1024 / 1024} MiB and was not scanned for secrets";
            yield break;
        }
        if (Array.IndexOf(content, (byte)0, 0, Math.Min(content.Length, 8192)) >= 0)
        {
            yield break; // binary
        }

        var text = Encoding.UTF8.GetString(content);
        var secrets = knownSecrets.Where(secret => secret.Length >= 8).ToList();
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            foreach (var (rule, pattern) in Rules)
            {
                if (pattern.IsMatch(line))
                {
                    yield return $"possible secret ({rule}) in {path}:{lineNumber}";
                }
            }
            if (secrets.Any(secret => line.Contains(secret, StringComparison.Ordinal)))
            {
                yield return $"the factory's own credential appears in {path}:{lineNumber}";
            }
        }
    }
}
