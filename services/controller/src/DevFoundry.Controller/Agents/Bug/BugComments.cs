using System.Text;

namespace DevFoundry.Controller;

/// <summary>What the ticket is told at each step of a bug run. Model text is made inert before it is posted.</summary>
public static class BugComments
{
    private static string Inert(string value) => TicketComments.Inert(value);

    public static string Analysis(BugAnalysis analysis)
    {
        var text = new StringBuilder();
        text.AppendLine(analysis.Verdict switch
        {
            BugAnalysis.Pass => $"**Bug analysis: understood (severity: {analysis.Severity})**",
            BugAnalysis.NeedsClarification => "**Bug analysis: I need more information**",
            _ => "**Bug analysis: this is not something I can fix**",
        });
        text.AppendLine();
        text.AppendLine(Inert(analysis.Summary));
        text.AppendLine();
        text.AppendLine($"- **Expected:** {Inert(analysis.Expected)}");
        text.AppendLine($"- **Actual:** {Inert(analysis.Actual)}");
        foreach (var hint in analysis.Hints)
        {
            text.AppendLine($"- Hint: {Inert(hint)}");
        }
        AppendQuestions(text, analysis.Questions);
        return text.ToString().TrimEnd();
    }

    public static string Reproduction(Reproduction reproduction)
    {
        var text = new StringBuilder();
        if (reproduction.Verdict == Controller.Reproduction.Reproduced)
        {
            text.AppendLine("**Reproduction: a failing test was written**");
            text.AppendLine();
            text.AppendLine(Inert(reproduction.Summary));
            text.AppendLine();
            text.AppendLine($"Test command: `{Inert(string.Join(' ', reproduction.TestCommand))}`");
            text.AppendLine($"Test files: {string.Join(", ", reproduction.TestFiles.Select(file => $"`{Inert(file)}`"))}");
            text.AppendLine();
            text.AppendLine("The factory will now run it on the unchanged code; it has to fail.");
        }
        else
        {
            text.AppendLine("**Reproduction: I could not reproduce the bug**");
            text.AppendLine();
            text.AppendLine(Inert(reproduction.Summary));
            AppendQuestions(text, reproduction.Questions);
        }
        return text.ToString().TrimEnd();
    }

    public static string Red(IReadOnlyList<string> command, long exitCode, string output) =>
        $"**Reproduction confirmed:** `{Inert(string.Join(' ', command))}` fails on the unchanged code (exit code {exitCode}).\n\n```\n{Fence(Tail(output, 600))}\n```";

    public static string Diagnosis(Diagnosis diagnosis)
    {
        var text = new StringBuilder();
        text.AppendLine(diagnosis.Verdict switch
        {
            Controller.Diagnosis.Pass => "**Root cause found**",
            Controller.Diagnosis.NeedsClarification => "**Diagnosis: I need more information**",
            _ => "**Diagnosis: the fix is larger than a bug fix**",
        });
        text.AppendLine();
        text.AppendLine(Inert(diagnosis.RootCause));
        foreach (var location in diagnosis.Locations)
        {
            text.AppendLine($"- `{Inert(location.Path)}`: {Inert(location.Note)}");
        }
        text.AppendLine();
        text.AppendLine($"Suggested fix: {Inert(diagnosis.FixOutline)}");
        if (diagnosis.Verdict == Controller.Diagnosis.TooLarge)
        {
            text.AppendLine();
            text.AppendLine("This needs design decisions or touches several components, so I am stopping. Open a feature ticket for it (the failing test and this analysis are a good start).");
        }
        AppendQuestions(text, diagnosis.Questions);
        return text.ToString().TrimEnd();
    }

    private static void AppendQuestions(StringBuilder text, IReadOnlyList<string> questions)
    {
        if (questions.Count == 0)
        {
            return;
        }
        text.AppendLine();
        text.AppendLine("**Questions**");
        for (var i = 0; i < questions.Count; i++)
        {
            text.AppendLine($"{i + 1}. {Inert(questions[i])}");
        }
        text.AppendLine();
        text.AppendLine("Reply on this ticket and I will continue.");
    }

    private static string Tail(string text, int max) => text.Length <= max ? text.Trim() : "..." + text[^max..].Trim();

    /// <summary>Check output sits in a fenced block, so it must not be able to close that block.</summary>
    private static string Fence(string text) => text.Replace("```", "'''");
}
