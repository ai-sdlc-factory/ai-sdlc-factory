using System.Text;
using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

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
