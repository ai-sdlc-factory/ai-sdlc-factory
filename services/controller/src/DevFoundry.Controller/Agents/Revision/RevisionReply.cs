using System.Text;

namespace DevFoundry.Controller;

/// <summary>The comment the factory posts on the pull request when a revision ends. Model text is made inert before it is posted.</summary>
public static class RevisionReply
{
    /// <summary>Hidden marker on every revision reply; the number of them on a pull request is the number of revisions made.</summary>
    public const string Marker = "<!-- dev-foundry:revision -->";

    public static string Pushed(string commit, RevisionReport report, IReadOnlyList<RevisionComment> comments, string checks, IReadOnlyList<string> reviewNotes, int rebasedOver = 0)
    {
        var text = new StringBuilder();
        text.AppendLine(Marker);
        text.AppendLine($"**Revision pushed: `{TicketComments.Inert(commit.Length > 8 ? commit[..8] : commit)}`**");
        text.AppendLine();
        text.AppendLine(TicketComments.Inert(report.Summary));
        AppendTable(text, report, comments);
        text.AppendLine();
        text.AppendLine($"Checks: {TicketComments.Inert(checks)}.");
        if (rebasedOver > 0)
        {
            text.AppendLine();
            text.AppendLine($"**The branch had {rebasedOver} new commit{(rebasedOver == 1 ? "" : "s")} from a person while I worked.** I put my commit on top of {(rebasedOver == 1 ? "it" : "them")}. The checks ran before that, so please look at the result.");
        }
        foreach (var note in reviewNotes)
        {
            text.AppendLine($"- {TicketComments.Inert(note)}");
        }
        text.AppendLine();
        text.Append("A person should look at the new commit and review again. I do not merge.");
        return text.ToString();
    }

    /// <summary>
    /// What to say in a comment's own conversation: the outcome in a sentence. Only line comments get one; the summary comment on the pull
    /// request covers the rest. Model text is made inert.
    /// </summary>
    public static IReadOnlyList<CommentReply> ThreadReplies(RevisionReport report, IReadOnlyList<RevisionComment> comments, string? commit)
    {
        var replies = new List<CommentReply>();
        foreach (var response in report.Responses)
        {
            var comment = comments.FirstOrDefault(c => c.Index == response.Index);
            if (comment is null || comment.Path is null || comment.Line is null || comment.CommitId is null)
            {
                continue;
            }
            var note = TicketComments.Inert(response.Note);
            var short8 = commit is null ? "" : commit.Length > 8 ? commit[..8] : commit;
            var text = response.Status switch
            {
                RevisionResponse.Fixed => commit is null ? "Fixed." : $"Fixed in `{short8}`." + (note.Length > 0 ? $" {note}" : ""),
                RevisionResponse.Answered => note,
                RevisionResponse.Declined => $"Not changed: {note}",
                _ => $"I need to know: {note}",
            };
            replies.Add(new CommentReply(comment.Path, comment.Line.Value, comment.OldSide, comment.CommitId,
                text + (response.Status == RevisionResponse.Fixed ? "\n\nPlease mark this conversation resolved if it looks right; I cannot." : "")));
        }
        return replies;
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
