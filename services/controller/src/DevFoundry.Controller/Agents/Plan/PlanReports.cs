using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

using static DevFoundry.Controller.JsonFields;

namespace DevFoundry.Controller;

/// <summary>
/// One ticket the planner proposes. <see cref="DependsOn"/> holds the numbers (starting at 1) of earlier tickets in the same plan that
/// must be built first; the factory does not react to merges yet, so only tickets without dependencies are created by a plain approval.
/// </summary>
public sealed record PlannedTicket(string Title, string Summary, IReadOnlyList<string> AcceptanceCriteria, IReadOnlyList<int> DependsOn);

/// <summary>The planner's split of a large request into feature tickets, for a person to approve.</summary>
public sealed record TicketPlan(string Verdict, string Summary, IReadOnlyList<PlannedTicket> Tickets, IReadOnlyList<string> Questions)
{
    public const string Pass = "pass";
    public const string NeedsClarification = "needs_clarification";
    public const string Reject = "reject";

    /// <summary>The tickets a plain <c>/approve</c> creates: the ones that wait for nothing.</summary>
    public IEnumerable<int> Ready => Tickets.Select((ticket, index) => (ticket, number: index + 1)).Where(item => item.ticket.DependsOn.Count == 0).Select(item => item.number);
}

/// <summary>Validates the planner's answer. Every limit here is also written in agents/agents/planner.md.</summary>
public static class PlanParser
{
    public const int MaxTickets = 8;
    public const int MaxTitle = 120;
    public const int MaxSummary = 1500;
    public const int MaxCriteria = 8;
    public const int MaxCriterion = 300;
    public const int MaxDependencies = 5;

    public static bool TryParse(string? text, [NotNullWhen(true)] out TicketPlan? value, out string error) =>
        AnswerReader.TryRead<TicketPlan>(text, Validate, out value, out error);

    private static bool Validate(JsonElement root, [NotNullWhen(true)] out TicketPlan? value, out string error)
    {
        value = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (TicketPlan.Pass or TicketPlan.NeedsClarification or TicketPlan.Reject))
        {
            error = "\"verdict\" must be one of pass, needs_clarification, reject";
            return false;
        }
        var summary = Text(root, "summary")?.Trim() ?? "";
        if (summary.Length is 0 or > MaxSummary)
        {
            error = $"\"summary\" must be a non-empty string of at most {MaxSummary} characters";
            return false;
        }
        if (!TextList(root, "questions", out var questions, out error, 5))
        {
            return false;
        }
        if (!root.TryGetProperty("tickets", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxTickets)
        {
            error = $"\"tickets\" must be an array of at most {MaxTickets} objects";
            return false;
        }

        var tickets = new List<PlannedTicket>();
        var number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = $"\"tickets\" item {number} must be an object";
                return false;
            }
            var title = Text(item, "title")?.Trim() ?? "";
            var ticketSummary = Text(item, "summary")?.Trim() ?? "";
            if (title.Length is 0 or > MaxTitle || ticketSummary.Length is 0 or > MaxSummary)
            {
                error = $"\"tickets\" item {number} needs a \"title\" of at most {MaxTitle} characters and a \"summary\" of at most {MaxSummary} characters, both non-empty";
                return false;
            }
            if (!TextList(item, "acceptance_criteria", out var criteria, out var listError, MaxCriteria, MaxCriterion) || criteria.Count == 0)
            {
                error = $"\"tickets\" item {number}: \"acceptance_criteria\" must list 1 to {MaxCriteria} criteria of at most {MaxCriterion} characters" + (listError == "" ? "" : $" ({listError})");
                return false;
            }
            if (!TryReadDependencies(item, number, out var dependsOn, out error))
            {
                return false;
            }
            tickets.Add(new PlannedTicket(title, ticketSummary, criteria, dependsOn));
        }

        if (verdict == TicketPlan.Pass && tickets.Count == 0)
        {
            error = "with verdict pass, \"tickets\" needs at least one entry (use needs_clarification or reject if you cannot plan)";
            return false;
        }
        if (verdict == TicketPlan.NeedsClarification && questions.Count == 0)
        {
            error = "with verdict needs_clarification, \"questions\" must not be empty";
            return false;
        }
        value = new TicketPlan(verdict, summary, tickets, questions);
        return true;
    }

    /// <summary>A ticket may depend only on tickets listed before it, so the order is the build order and a cycle is impossible.</summary>
    private static bool TryReadDependencies(JsonElement item, int number, out IReadOnlyList<int> dependsOn, out string error)
    {
        var list = new List<int>();
        dependsOn = list;
        error = "";
        if (!item.TryGetProperty("depends_on", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxDependencies)
        {
            error = $"\"tickets\" item {number}: \"depends_on\" must be an array of at most {MaxDependencies} numbers (use [] when it waits for nothing)";
            return false;
        }
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetInt32(out var other) || other < 1 || other >= number || list.Contains(other))
            {
                error = $"\"tickets\" item {number}: \"depends_on\" may only name different, earlier tickets by their number (1 to {number - 1})";
                return false;
            }
            list.Add(other);
        }
        return true;
    }
}

/// <summary>A person's approval of tickets in a plan: <c>/approve</c> for every ticket that waits for nothing, or <c>/approve 2 3</c> for those named.</summary>
public sealed record PlanApproval(IReadOnlyList<int> Numbers);

public static partial class PlanCommands
{
    [GeneratedRegex(@"^[ \t]*/approve\b(?<rest>[^\r\n]*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Line();

    [GeneratedRegex(@"\A[ \t,#]*(?:\d{1,3}[ \t,#]*)*\z")]
    private static partial Regex Numbers();

    /// <summary>
    /// The approval in a comment, or null when it has none. A line such as <c>/approve please</c> is not an approval, so it cannot create
    /// tickets by accident; it counts as feedback for the planner.
    /// </summary>
    public static PlanApproval? Parse(string? body)
    {
        var numbers = new List<int>();
        var found = false;
        foreach (Match match in Line().Matches(body ?? ""))
        {
            var rest = match.Groups["rest"].Value;
            if (!Numbers().IsMatch(rest))
            {
                continue;
            }
            found = true;
            foreach (Match digits in Regex.Matches(rest, @"\d{1,3}"))
            {
                var number = int.Parse(digits.Value);
                if (!numbers.Contains(number))
                {
                    numbers.Add(number);
                }
            }
        }
        return found ? new PlanApproval(numbers) : null;
    }
}
