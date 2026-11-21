
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
