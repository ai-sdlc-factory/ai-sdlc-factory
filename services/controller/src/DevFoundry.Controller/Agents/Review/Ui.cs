using System.Text;
using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>How much of a UI change makes it "medium" or "large": either number is enough.</summary>
public sealed record UiThreshold(int Files, int Lines);

/// <summary>
/// What a project says about its user interface: which files are UI, and how big a UI change has to be before
/// a UX review is worth asking for. Absent from the project file means the project has no UI to review.
/// </summary>
public sealed record UiConfig(IReadOnlyList<string> Paths, UiThreshold Medium, UiThreshold Large)
{
    public static readonly UiThreshold DefaultMedium = new(3, 40);
    public static readonly UiThreshold DefaultLarge = new(8, 200);
}

/// <summary>
/// File path patterns. <c>*</c> matches within one folder level, <c>**</c> across levels (<c>**/</c> also
/// matches no folder at all), <c>?</c> one character. A pattern without a slash is matched against the file
/// name wherever the file is, so <c>*.css</c> means every stylesheet.
/// </summary>
public static partial class Globs
{
    [GeneratedRegex(@"\A[A-Za-z0-9._/*?@+-]{1,100}\z")]
    private static partial Regex Allowed();

    public static bool IsValidPattern(string pattern) =>
        Allowed().IsMatch(pattern) && !pattern.StartsWith('/') && !pattern.Contains("..");

    public static bool IsMatch(string path, string pattern)
    {
        var target = pattern.Contains('/') ? path : path[(path.LastIndexOf('/') + 1)..];
        return ToRegex(pattern).IsMatch(target);
    }

    public static bool IsMatchAny(string path, IEnumerable<string> patterns) => patterns.Any(pattern => IsMatch(path, pattern));

    private static Regex ToRegex(string pattern)
    {
        var text = new StringBuilder("\\A");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                {
                    text.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    text.Append(".*");
                    i += 1;
                }
            }
            else if (c == '*')
            {
                text.Append("[^/]*");
            }
            else if (c == '?')
            {
                text.Append("[^/]");
            }
            else
            {
                text.Append(Regex.Escape(c.ToString()));
            }
        }
        text.Append("\\z");
        return new Regex(text.ToString(), RegexOptions.CultureInvariant);
    }
}

/// <summary>Reads the unified diff the controller produces for reviewers (see <c>GitWorkspace.DiffAsync</c>).</summary>
public static partial class UnifiedDiff
{
    /// <summary>The diff is cut at this many characters, so a diff this long is bigger than it looks.</summary>
    public const int MaxChars = 200_000;

    public sealed record FileDiff(string Path, string Text, int Changed);

    [GeneratedRegex(@"\Adiff --git a/(?<a>.+) b/(?<b>.+)\z")]
    private static partial Regex Header();

    [GeneratedRegex(@"large: (?<lines>\d+) lines")]
    private static partial Regex NotShownLines();

    public static IReadOnlyList<FileDiff> Split(string diff)
    {
        var files = new List<FileDiff>();
        string? path = null;
        var text = new StringBuilder();
        var changed = 0;
        var inHunk = false;

        void Finish()
        {
            if (path is not null)
            {
                files.Add(new FileDiff(path, text.ToString().TrimEnd('\n'), changed));
            }
            text.Clear();
            changed = 0;
            inHunk = false;
        }

        foreach (var line in diff.Split('\n'))
        {
            var header = Header().Match(line);
            if (header.Success)
            {
                Finish();
                path = header.Groups["b"].Value;
            }
            else if (path is not null)
            {
                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    inHunk = true; // what follows are content lines, even when the file has no @@ markers
                }
                else if (inHunk && line.Length > 0 && (line[0] == '+' || line[0] == '-'))
                {
                    changed++;
                }
                else if (NotShownLines().Match(line) is { Success: true } large)
                {
                    changed += int.Parse(large.Groups["lines"].Value);
                }
            }
            if (path is not null)
            {
                text.Append(line).Append('\n');
            }
        }
        Finish();
        return files;
    }

    /// <summary>Only the parts of the diff that belong to the given files.</summary>
    public static string Only(string diff, IEnumerable<string> paths)
    {
        var wanted = paths.ToHashSet(StringComparer.Ordinal);
        return string.Join('\n', Split(diff).Where(file => wanted.Contains(file.Path)).Select(file => file.Text));
    }
}

public enum UiSize
{
    None,
    Small,
    Medium,
    Large,
}

/// <summary>
/// Decides, by counting, whether a change is big enough in the user interface for a UX review. No model is
/// involved, so the same change always gets the same answer.
/// </summary>
public sealed record UiChange(UiSize Size, IReadOnlyList<string> Files, int Lines)
{
    public bool ShouldReview => Size >= UiSize.Medium;

    public string Describe() => $"{Files.Count} UI file(s), {Lines} changed line(s)";

    public static UiChange Classify(IEnumerable<FileChange> changed, string diff, UiConfig ui)
    {
        var files = changed.Select(file => file.Path).Where(path => Globs.IsMatchAny(path, ui.Paths)).ToList();
        if (files.Count == 0)
        {
            return new UiChange(UiSize.None, [], 0);
        }

        // A diff that hit the cap is bigger than it looks, so its line count cannot be trusted: call it large.
        var truncated = diff.Length >= UnifiedDiff.MaxChars;
        var lines = UnifiedDiff.Split(diff).Where(file => files.Contains(file.Path)).Sum(file => file.Changed);
        var size = truncated || files.Count >= ui.Large.Files || lines >= ui.Large.Lines ? UiSize.Large
            : files.Count >= ui.Medium.Files || lines >= ui.Medium.Lines ? UiSize.Medium
            : UiSize.Small;
        return new UiChange(size, files, truncated ? Math.Max(lines, ui.Large.Lines) : lines);
    }
}
