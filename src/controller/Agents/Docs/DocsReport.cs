using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using static AiSdlcFactory.Controller.JsonFields;

namespace AiSdlcFactory.Controller;

/// <summary>The documentation writer's answer. What changed is read from the workspace by the factory; the writer's word is only a summary.</summary>
public sealed record DocsReport(string Verdict, string Summary, IReadOnlyList<string> Notes)
{
    /// <summary>The documents were written or updated.</summary>
    public const string Done = "done";

    /// <summary>The documents already say what the repository does, so nothing was changed.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>The writer cannot continue (the repository is too thin to describe, or something it may not change is in the way).</summary>
    public const string Blocked = "blocked";
}

public static class DocsParser
{
    public static bool TryParse(string? text, [NotNullWhen(true)] out DocsReport? report, out string error) =>
        AnswerReader.TryRead<DocsReport>(text, Validate, out report, out error);

    private static bool Validate(JsonElement root, [NotNullWhen(true)] out DocsReport? report, out string error)
    {
        report = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (DocsReport.Done or DocsReport.Unchanged or DocsReport.Blocked))
        {
            error = "\"verdict\" must be one of done, unchanged, blocked";
            return false;
        }
        var summary = Text(root, "summary")?.Trim();
        if (string.IsNullOrEmpty(summary) || summary.Length > 2000)
        {
            error = "\"summary\" must be a non-empty string of at most 2000 characters";
            return false;
        }
        if (!TextList(root, "notes", out var notes, out error, maxItems: 10, maxLength: 500))
        {
            return false;
        }
        report = new DocsReport(verdict, summary, notes);
        return true;
    }
}

/// <summary>The files a documentation run may write: the repository's agent instructions and overview, and Markdown under docs/. Nothing else, and nothing deleted.</summary>
public static class DocsFiles
{
    private static readonly string[] Root = ["AGENTS.md", "README.md", "CONTRIBUTING.md"];

    public static bool IsAllowed(string path) =>
        Root.Contains(path, StringComparer.Ordinal)
        || (path.StartsWith("docs/", StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

    /// <summary>Why a change cannot be published, or null when every changed file is one a documentation run may write.</summary>
    public static string? Problem(IEnumerable<FileChange> files)
    {
        var bad = files.Where(file => file.Kind == FileChange.Deleted || !IsAllowed(file.Path)).Select(file => $"{file.Kind} {file.Path}").Take(10).ToList();
        return bad.Count == 0
            ? null
            : $"You may only add or change AGENTS.md, README.md, CONTRIBUTING.md and Markdown files under docs/, and never delete one. These changes are not allowed: {string.Join(", ", bad)}. Undo them.";
    }
}
