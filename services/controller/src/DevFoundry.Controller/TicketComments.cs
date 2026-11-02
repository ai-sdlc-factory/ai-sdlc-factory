using System.Text;

namespace DevFoundry.Controller;

/// <summary>Formats what the factory says on a ticket. Model text is untrusted, so it is made inert.</summary>
public static class TicketComments
{
    public static string Analysis(AnalystVerdict verdict)
    {
        var text = new StringBuilder();
        switch (verdict.Verdict)
        {
            case AnalystVerdict.Pass:
                text.AppendLine("**Analysis: ready to build**");
                break;
            case AnalystVerdict.NeedsClarification:
                text.AppendLine("**Analysis: I need answers before building**");
                break;
            default:
                text.AppendLine("**Analysis: not buildable as written**");
                break;
        }
        text.AppendLine();
        text.AppendLine(Inert(verdict.Summary));

        if (verdict.AcceptanceCriteria.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Acceptance criteria**");
            foreach (var criterion in verdict.AcceptanceCriteria)
            {
                text.AppendLine($"- {Inert(criterion)}");
            }
        }
        if (verdict.Questions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Questions**");
            for (var i = 0; i < verdict.Questions.Count; i++)
            {
                text.AppendLine($"{i + 1}. {Inert(verdict.Questions[i])}");
            }
            text.AppendLine();
            text.AppendLine("Reply on this ticket and I will continue.");
        }

        var c = verdict.Complexity;
        text.AppendLine();
        text.Append($"Estimate: about {c.FilesEstimate} file(s); several projects: {YesNo(c.MultipleProjects)}; " +
                    $"public API change: {YesNo(c.PublicApiChange)}; data model change: {YesNo(c.DataModelChange)}; " +
                    $"new dependency: {YesNo(c.NewDependency)}.");
        return text.ToString().TrimEnd();
    }

    /// <summary>Stops text from notifying people (@name) or starting its own lines/blocks.</summary>
    public static string Inert(string value) =>
        value.Replace("@", "@​").Replace("\r", " ").Replace("\n", " ").Replace("`", "'").Trim();

    private static string YesNo(bool value) => value ? "yes" : "no";
}
