using System.Text.RegularExpressions;

namespace AiSdlcFactory.Controller;

/// <summary>
/// A person's go-ahead for the analyst's acceptance criteria, when the project asks for one (<c>confirm_criteria</c>): a line that is only <c>/approve</c>. Anything else
/// (including <c>/approve please</c>) is not an approval; it is read by the analyst as a change to ask for.
/// </summary>
public static partial class CriteriaApproval
{
    [GeneratedRegex(@"^[ \t]*/approve[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Line();

    public static bool IsApproval(string? body) => Line().IsMatch((body ?? "").Replace("\r", ""));
}
