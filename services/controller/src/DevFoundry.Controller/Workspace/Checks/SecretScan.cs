using System.Text;
using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>
/// A deterministic scan of changed file contents for credentials. It is a safety net, not a guarantee:
/// it catches common token shapes and the factory's own token, and reports where, never the value.
/// </summary>
public static partial class SecretScan
{
    public const int MaxScanBytes = 1024 * 1024;

    public static string OversizeMessage(string path) => $"{path} is larger than {MaxScanBytes / 1024 / 1024} MiB and was not scanned for secrets";

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
            yield return OversizeMessage(path);
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
