using System.Text;

namespace DevFoundry.Controller;

/// <summary>The comments the factory posts where <c>/update</c> was asked. Model and repository text is made inert before it is posted.</summary>
public static class UpdateReply
{
    /// <summary>Hidden marker on the comment that ends an update. It is not the revision marker, so an update never counts as a revision.</summary>
    public const string Marker = "<!-- dev-foundry:update -->";

    public static string UpToDate(string baseBranch) =>
        $"{Marker}\n**Already up to date**\n\nThis pull request's branch already has everything that `{TicketComments.Inert(baseBranch)}` has, so there is nothing to merge.";

    public static string Starting(string baseBranch, int pullNumber) =>
        $"I am merging `{TicketComments.Inert(baseBranch)}` into the branch of pull request #{pullNumber}.";

    public static string Merged(string baseBranch, MergeResult merge) =>
        $"`{TicketComments.Inert(baseBranch)}` merged without conflicts ({merge.FilesBrought} file{(merge.FilesBrought == 1 ? "" : "s")} came in). Running the project's checks on the result.";

    public static string Conflicted(string baseBranch, IReadOnlyList<ConflictedFile> conflicts)
    {
        var text = new StringBuilder();
        text.AppendLine($"Merging `{TicketComments.Inert(baseBranch)}` left {conflicts.Count} file{(conflicts.Count == 1 ? "" : "s")} in conflict. The resolver is working on {(conflicts.Count == 1 ? "it" : "them")}:");
        foreach (var conflict in conflicts.Take(20))
        {
            text.AppendLine($"- `{TicketComments.Inert(conflict.Path)}`: {conflict.Kind}");
        }
        if (conflicts.Count > 20)
        {
            text.AppendLine($"- and {conflicts.Count - 20} more");
        }
        return text.ToString().TrimEnd();
    }

    public static string MergeFailed(string baseBranch, string reason) =>
        $"**Update: I could not merge**\n\nGit could not merge `{TicketComments.Inert(baseBranch)}` into this branch: {TicketComments.Inert(reason)} A person needs to merge it by hand.";

    public static string TooManyConflicts(string baseBranch, int count) =>
        $"**Update: too many conflicts**\n\nMerging `{TicketComments.Inert(baseBranch)}` leaves {count} files in conflict, more than the {GitWorkspace.MaxConflictedFiles} I will try. A person should merge it by hand, or rebase the work on a newer base.";

    public static string ProtectedConflicts(string baseBranch, IReadOnlyList<string> paths) =>
        $"**Update: a person has to resolve this**\n\nMerging `{TicketComments.Inert(baseBranch)}` leaves protected files in conflict, and only a person may change those: " +
        string.Join(", ", paths.Take(10).Select(path => $"`{TicketComments.Inert(path)}`")) + ". Nothing was pushed.";

    public static string Pushed(string commit, string baseBranch, MergeResult merge, MergeReport? report, string checks, IReadOnlyList<string> reviewNotes)
    {
        var text = new StringBuilder();
        text.AppendLine(Marker);
        text.AppendLine($"**Updated from `{TicketComments.Inert(baseBranch)}`: `{TicketComments.Inert(commit.Length > 8 ? commit[..8] : commit)}`**");
        text.AppendLine();
        text.AppendLine($"I merged `{TicketComments.Inert(baseBranch)}` (at `{TicketComments.Inert(merge.BaseCommit.Length > 8 ? merge.BaseCommit[..8] : merge.BaseCommit)}`) into this branch as a merge commit; {merge.FilesBrought} file{(merge.FilesBrought == 1 ? "" : "s")} came in from it.");
        if (report is not null)
        {
            text.AppendLine();
            text.AppendLine(TicketComments.Inert(report.Summary));
        }
        if (report is { Resolutions.Count: > 0 })
        {
            text.AppendLine();
            text.AppendLine("| File | What I did |");
            text.AppendLine("| --- | --- |");
            foreach (var resolution in report.Resolutions)
            {
                var conflict = merge.Conflicts.FirstOrDefault(c => c.Path == resolution.Path);
                var was = conflict is null ? "" : $" ({TicketComments.Inert(conflict.Kind)})";
                text.AppendLine($"| `{TicketComments.Inert(resolution.Path)}`{was} | {resolution.How.Replace('_', ' ')}: {TicketComments.Inert(resolution.Note).Replace("|", "\\|")} |");
            }
        }
        text.AppendLine();
        text.AppendLine($"Checks: {TicketComments.Inert(checks)}.");
        foreach (var note in reviewNotes)
        {
            text.AppendLine($"- {TicketComments.Inert(note)}");
        }
        text.AppendLine();
        text.Append("A person should look at the merge commit. Forgejo may take a moment to show that the pull request can be merged. I do not merge the pull request.");
        return text.ToString();
    }
}
