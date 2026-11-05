using System.Text;

namespace DevFoundry.Controller;

/// <summary>The comment the factory posts on the pull request when a revision ends. Model text is made inert before it is posted.</summary>
public static class RevisionReply
{
    /// <summary>Hidden marker on every revision reply; the number of them on a pull request is the number of revisions made.</summary>
    public const string Marker = "<!-- dev-foundry:revision -->";

    public static string Pushed(string commit, RevisionReport report, IReadOnlyList<RevisionComment> comments, string checks, IReadOnlyList<string> reviewNotes)
    {
        var text = new StringBuilder();
        text.AppendLine(Marker);
        text.AppendLine($"**Revision pushed: `{TicketComments.Inert(commit.Length > 8 ? commit[..8] : commit)}`**");
        text.AppendLine();
        text.AppendLine(TicketComments.Inert(report.Summary));
        AppendTable(text, report, comments);
        text.AppendLine();
        text.AppendLine($"Checks: {TicketComments.Inert(checks)}.");
        foreach (var note in reviewNotes)
        {
            text.AppendLine($"- {TicketComments.Inert(note)}");
        }
        text.AppendLine();
        text.Append("A person should look at the new commit and review again. I do not merge.");
        return text.ToString();
    }

    public static string Replied(RevisionReport report, IReadOnlyList<RevisionComment> comments)
    {
        var text = new StringBuilder();
        text.AppendLine(Marker);
        text.AppendLine("**Revision: no code change**");
        text.AppendLine();
        text.AppendLine(TicketComments.Inert(report.Summary));
        AppendTable(text, report, comments);
        text.AppendLine();
        text.Append("Nothing was pushed. Reply with `/fix` and what to change if you want a change anyway.");
        return text.ToString();
    }

    private static void AppendTable(StringBuilder text, RevisionReport report, IReadOnlyList<RevisionComment> comments)
    {
        text.AppendLine();
        text.AppendLine("| # | Comment | What I did |");
        text.AppendLine("| --- | --- | --- |");
        foreach (var response in report.Responses)
        {
            var comment = comments.FirstOrDefault(c => c.Index == response.Index);
            var quote = comment is null ? "" : TicketComments.Inert(comment.Body.Length > 90 ? comment.Body[..90] + "..." : comment.Body).Replace("|", "\\|");
            var where = comment is null ? "" : $"`{TicketComments.Inert(comment.Where)}` ";
            var did = response.Status.Replace('_', ' ') + (response.Note.Length > 0 ? ": " + TicketComments.Inert(response.Note).Replace("|", "\\|") : "");
            text.AppendLine($"| {response.Index} | {where}{quote} | {did} |");
        }
    }
}
