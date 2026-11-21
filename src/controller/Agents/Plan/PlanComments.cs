using System.Text;

namespace DevFoundry.Controller;

/// <summary>A ticket the factory opened for a plan item.</summary>
public sealed record CreatedTicket(int PlanNumber, int IssueNumber, string Url);

/// <summary>What the plan ticket is told, and the text of the tickets the plan creates. Model text is made inert before it is posted.</summary>
public static class PlanComments
{
    private static string Inert(string value) => TicketComments.Inert(value);

    public static string Proposal(TicketPlan plan, IReadOnlyDictionary<int, CreatedTicket>? created = null)
    {
        created ??= new Dictionary<int, CreatedTicket>();
        var text = new StringBuilder();
        text.AppendLine(plan.Verdict switch
        {
            TicketPlan.Pass => "**Plan: proposed tickets**",
            TicketPlan.NeedsClarification => "**Plan: I need more information**",
            _ => "**Plan: this is not something I can split into tickets**",
        });
        text.AppendLine();
        text.AppendLine(Inert(plan.Summary));
        foreach (var (ticket, number) in plan.Tickets.Select((t, i) => (t, i + 1)))
        {
            text.AppendLine();
            var state = created.TryGetValue(number, out var made) ? $" (created: #{made.IssueNumber})" : "";
            text.AppendLine($"**{number}. {Inert(ticket.Title)}**{state}");
            text.AppendLine(Inert(ticket.Summary));
            if (ticket.DependsOn.Count > 0)
            {
                text.AppendLine($"- Waits for: {string.Join(", ", ticket.DependsOn.Select(n => $"{n}"))}");
            }
            foreach (var criterion in ticket.AcceptanceCriteria)
            {
                text.AppendLine($"- {Inert(criterion)}");
            }
        }
        if (plan.Tickets.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Nothing is created until a person approves. Reply with:");
            var ready = plan.Ready.ToList();
            text.AppendLine($"- `/approve` to create the tickets that wait for nothing ({string.Join(", ", ready)}); each one then starts the feature workflow and uses plan tokens;");
            text.AppendLine("- `/approve 2 3` to create only the tickets named (a ticket that waits for another can be named once that one is merged);");
            text.AppendLine("- or any other comment to change the plan, and I will plan again (3 times at most).");
        }
        if (plan.Questions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Questions**");
            for (var i = 0; i < plan.Questions.Count; i++)
            {
                text.AppendLine($"{i + 1}. {Inert(plan.Questions[i])}");
            }
            text.AppendLine();
            text.AppendLine("Reply on this ticket and I will continue.");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>The title and body of the ticket for one plan item, with what it depends on written as ticket numbers where those exist.</summary>
    public static (string Title, string Body) NewTicket(
        WorkRequest plan, TicketPlan proposal, int number, IReadOnlyDictionary<int, CreatedTicket> created, string approver)
    {
        var ticket = proposal.Tickets[number - 1];
        var body = new StringBuilder();
        body.AppendLine($"Opened by the factory from plan #{plan.IssueNumber}, at the request of {Inert(string.IsNullOrWhiteSpace(approver) ? "a person" : approver)}. Item {number} of {proposal.Tickets.Count}.");
        body.AppendLine();
        body.AppendLine("## What is wanted");
        body.AppendLine(Inert(ticket.Summary));
        body.AppendLine();
        body.AppendLine("## Acceptance criteria");
        foreach (var criterion in ticket.AcceptanceCriteria)
        {
            body.AppendLine($"- {Inert(criterion)}");
        }
        if (ticket.DependsOn.Count > 0)
        {
            body.AppendLine();
            body.AppendLine("## Depends on");
            foreach (var other in ticket.DependsOn)
            {
                var name = Inert(proposal.Tickets[other - 1].Title);
                body.AppendLine(created.TryGetValue(other, out var made) ? $"- #{made.IssueNumber}: {name}" : $"- {name} (plan item {other}, no ticket yet)");
            }
        }
        var title = Inert(ticket.Title);
        return (title.Length > PlanParser.MaxTitle ? title[..PlanParser.MaxTitle] : title, body.ToString().TrimEnd());
    }

    public static string Created(IReadOnlyList<CreatedTicket> now, TicketPlan plan, IReadOnlyDictionary<int, CreatedTicket> all)
    {
        var text = new StringBuilder();
        text.AppendLine($"**Created {now.Count} ticket{(now.Count == 1 ? "" : "s")} from this plan**");
        text.AppendLine();
        foreach (var item in now)
        {
            text.AppendLine($"- Item {item.PlanNumber}: {Inert(plan.Tickets[item.PlanNumber - 1].Title)}: {item.Url}");
        }
        text.AppendLine();
        text.AppendLine("Each ticket carries the `feature` label, so the feature workflow starts on it. A person still reviews and merges every pull request.");
        var waiting = Enumerable.Range(1, plan.Tickets.Count).Where(n => !all.ContainsKey(n)).ToList();
        if (waiting.Count > 0)
        {
            text.AppendLine();
            var free = waiting.Where(n => plan.Tickets[n - 1].DependsOn.Count == 0).ToList();
            var blocked = waiting.Except(free).ToList();
            text.Append($"Not created yet: {string.Join(", ", waiting)}.");
            if (free.Count > 0)
            {
                text.Append($" Reply `/approve` (or `/approve {free[0]}`) to create {(free.Count == 1 ? "the one" : "those")} that wait{(free.Count == 1 ? "s" : "")} for nothing.");
            }
            if (blocked.Count > 0)
            {
                text.Append($" Reply `/approve {blocked[0]}` (and so on) when the ticket it waits for is merged.");
            }
            text.Append(" Close this ticket to stop.");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>A comment that starts with /approve but is not one (<c>/approve please</c>). The plan is not changed and nothing is created.</summary>
    public const string UnclearApproval =
        "I did not understand that as an approval, so I changed nothing and created nothing. Write **`/approve`** alone to create every ticket that waits for nothing, or **`/approve 1 3`** to create exactly those (numbers as in the plan). To change the plan instead, say what to change.";

    public static string NothingToApprove(bool allCreated) => allCreated
        ? "Every ticket in this plan was already created."
        : "Every ticket that waits for nothing was already created. Name the number of one that waits, for example `/approve 2`.";

    public static string NoSuchTicket(IEnumerable<int> numbers, int count) =>
        $"There is no ticket {string.Join(", ", numbers)} in this plan. The tickets are 1 to {count}.";

    public static string AlreadyCreated(IEnumerable<int> numbers) =>
        $"Already created, so I left {(numbers.Count() == 1 ? "it" : "them")} alone: {string.Join(", ", numbers)}.";

    public static string NotCreated(string reason) =>
        $"I could not open the ticket: {reason}\n\nTickets created before this one stay. Reply `/approve` again to try the rest.";

    public const string ApprovalWhilePlanning =
        "An approval arrived while I was still planning, so I ignored it: it was not for the plan above. Read the plan, then reply `/approve` again.";

    public const string PlanIsFinal =
        "Tickets were already created from this plan, so I will not change it. Open a new plan ticket for further work, or reply `/approve N` for a ticket that has not been created.";

    public const string NothingToRefresh =
        "I have already changed the plan several times. Reply `/approve` or `/approve N`, or close the ticket.";
}
