using System.Text;
using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>
/// The factory's own run record (<c>.factory/runs/**</c>, committed to a pull request when a project sets <c>record</c>) is not part of what a pull request changes: it is the
/// factory's account of how the change was made. Left in, it made reviewers talk about "the accompanying records", crowded the code out of the diff the prompts carry
/// (<c>.factory</c> sorts first), and kept the "this pull request only changes the user interface" check from ever being true.
/// </summary>
public static partial class RunRecords
{
    public const string Folder = ".factory/runs/";

    public static bool IsRunRecord(string? path) => path is not null && path.StartsWith(Folder, StringComparison.Ordinal);

    [GeneratedRegex(@"(?m)^(?=diff --git )")]
    private static partial Regex Sections();

    /// <summary>The diff without the sections of run record files.</summary>
    public static string WithoutRecords(string diff)
    {
        if (!diff.Contains(Folder, StringComparison.Ordinal))
        {
            return diff;
        }
        var kept = new StringBuilder();
        foreach (var section in Sections().Split(diff))
        {
            var lineEnd = section.IndexOf('\n');
            var header = lineEnd < 0 ? section : section[..lineEnd];
            if (header.StartsWith("diff --git ", StringComparison.Ordinal) && (header.Contains($" a/{Folder}", StringComparison.Ordinal) || header.Contains($" b/{Folder}", StringComparison.Ordinal)))
            {
                continue;
            }
            kept.Append(section);
        }
        return kept.ToString();
    }
}
