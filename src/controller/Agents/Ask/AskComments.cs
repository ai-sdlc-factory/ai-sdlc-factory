using System.Text;

namespace AiSdlcFactory.Controller;

/// <summary>What <c>/ask</c> says on the ticket or pull request: how to use it, that it is busy, and the answer.</summary>
public static class AskComments
{
    public const string Usage = "Write your question after `/ask`, for example `/ask where is the login handled?`. I answer from the code in the repository and change nothing.";

    public const string Busy = "A question is already being answered here. Its answer will be posted when it is done; comment `/ask` again after that.";

    public static string Started(bool onPullRequest) =>
        $"I am reading the repository to answer your question{(onPullRequest ? " (and what this pull request changes)" : "")}. I only read: nothing will be changed.";

    /// <summary>The answer. It keeps its line breaks and code formatting (it is the point of the comment); only <c>@name</c> is made harmless, so nobody is notified by an answer.</summary>
    public static string Answer(AskAnswer answer, IReadOnlyCollection<string> readFiles)
    {
        var text = new StringBuilder();
        text.AppendLine(answer.Verdict == AskAnswer.Answered ? "**Answer**" : "**I could not answer this from the repository**");
        text.AppendLine();
        text.AppendLine(TicketComments.NoMentions(SecretScan.Redact(answer.Answer, [])));
        if (readFiles.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Files read: " + string.Join(", ", readFiles.Take(AskParser.MaxFiles).Select(path => $"`{path}`")));
        }
        text.AppendLine();
        text.Append("_Answered by an agent that read the repository; it can be wrong. Nothing was changed._");
        return text.ToString();
    }
}
