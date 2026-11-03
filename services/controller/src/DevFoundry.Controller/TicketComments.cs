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

    public static string Coverage(CoverageReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(report.Verdict switch
        {
            CoverageVerdicts.FullyCovered => "**Coverage check: already implemented**",
            CoverageVerdicts.PartlyCovered => "**Coverage check: partly implemented**",
            _ => "**Coverage check: not implemented yet**",
        });
        text.AppendLine();
        text.AppendLine(Inert(report.Summary));
        text.AppendLine();
        foreach (var criterion in report.Criteria)
        {
            text.Append($"- **{criterion.Status}**: {Inert(criterion.Criterion)}");
            if (criterion.Evidence.Count > 0)
            {
                // Evidence paths were validated to a safe character set, so they are safe in code spans.
                text.Append(" (" + string.Join(", ", criterion.Evidence.Select(path => $"`{path}`")) + ")");
            }
            if (criterion.Note.Length > 0)
            {
                text.Append($" - {Inert(criterion.Note)}");
            }
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    public static string Design(ArchitectDesign design)
    {
        var text = new StringBuilder();
        text.AppendLine(design.Verdict switch
        {
            ArchitectDesign.Pass => "**Design: ready to build**",
            ArchitectDesign.NeedsDecision => "**Design: a decision is needed**",
            _ => "**Design: not feasible as asked**",
        });
        text.AppendLine();
        text.AppendLine(Inert(design.Summary));
        if (design.Approach.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("**Approach**");
            text.AppendLine(Inert(design.Approach));
        }
        if (design.Components.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Components**");
            foreach (var component in design.Components)
            {
                // Paths were validated to a safe character set, so they are safe in code spans.
                text.AppendLine($"- `{component.Path}`: {Inert(component.Change)}");
            }
        }
        if (design.Risks.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Risks**");
            foreach (var risk in design.Risks)
            {
                text.AppendLine($"- {Inert(risk)}");
            }
        }
        if (design.Guidance.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Guidance for the build**");
            foreach (var item in design.Guidance)
            {
                text.AppendLine($"- {Inert(item)}");
            }
        }
        if (design.Questions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Questions**");
            for (var i = 0; i < design.Questions.Count; i++)
            {
                text.AppendLine($"{i + 1}. {Inert(design.Questions[i])}");
            }
            text.AppendLine();
            text.AppendLine("Reply on this ticket and I will continue.");
        }
        return text.ToString().TrimEnd();
    }

    public static string Review(string role, ReviewReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(report.Verdict == ReviewReport.Pass ? $"**Review ({role}): no blocking findings**" : $"**Review ({role}): changes needed**");
        text.AppendLine();
        text.AppendLine(Inert(report.Summary));
        foreach (var finding in report.Findings)
        {
            text.AppendLine($"- **{finding.Severity}**{(finding.Path is null ? "" : $" `{finding.Path}`")}: {Inert(finding.Message)}");
        }
        return text.ToString().TrimEnd();
    }

    public static string Qa(QaReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(report.Verdict == ReviewReport.Pass ? "**QA: every criterion is met**" : $"**QA: {report.NotMet.Count()} criterion not met**");
        text.AppendLine();
        text.AppendLine(Inert(report.Summary));
        foreach (var criterion in report.Criteria)
        {
            text.Append($"- **{criterion.Status.Replace('_', ' ')}**: {Inert(criterion.Criterion)}");
            if (criterion.Evidence.Count > 0)
            {
                text.Append(" (" + string.Join(", ", criterion.Evidence.Select(path => $"`{path}`")) + ")");
            }
            if (criterion.Note.Length > 0)
            {
                text.Append($" - {Inert(criterion.Note)}");
            }
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    public static string TriageDecision(TriageDecision decision) =>
        decision.NeedsArchitect
            ? "**Triage: design review needed before building**\n\nReasons: " + string.Join("; ", decision.Reasons) + "."
            : "**Triage: small change, no design review needed**\n\nNo rule asked for one: few files, one project, no API, data model or dependency change.";

    /// <summary>Stops text from notifying people (@name) or starting its own lines/blocks.</summary>
    public static string Inert(string value) =>
        value.Replace("@", "@​").Replace("\r", " ").Replace("\n", " ").Replace("`", "'").Trim();

    private static string YesNo(bool value) => value ? "yes" : "no";
}
