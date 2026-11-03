using System.Text;

namespace DevFoundry.Controller;

/// <summary>What goes back to the developer when a review or QA asks for changes.</summary>
public static class ReviewFeedback
{
    public static string? From(IEnumerable<(string Role, ReviewReport Report)> reviews, QaReport? qa)
    {
        var text = new StringBuilder();
        foreach (var (role, report) in reviews)
        {
            foreach (var finding in report.Blocking)
            {
                text.AppendLine($"[{role}, {finding.Severity}]{(finding.Path is null ? "" : $" {finding.Path}:")} {finding.Message}");
            }
        }
        if (qa is not null)
        {
            foreach (var criterion in qa.NotMet)
            {
                text.AppendLine($"[qa] criterion not met: {criterion.Criterion} - {criterion.Note}");
            }
        }
        return text.Length == 0 ? null : "The review asks for these changes:\n" + text.ToString().TrimEnd();
    }
}
