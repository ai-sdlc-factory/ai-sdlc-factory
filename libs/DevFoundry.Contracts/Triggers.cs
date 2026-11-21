using System.Text.Json;

namespace DevFoundry.Contracts;

/// <summary>
/// How a host's tickets say which workflow to run. Out of the box a ticket labelled <c>feature</c>, <c>bug</c>, <c>incident</c>, <c>plan</c> or <c>docs</c> starts that workflow. A host can add to that:
/// more label names (or tags, which an adapter reads as labels) for a kind, work-item types for a kind (for a host whose tickets have a type), and the <c>/factory &lt;kind&gt;</c> comment command
/// (off unless the operator turns it on, because anyone who can comment could then start a run, where only someone who can label could before). The names the factory knows always work as well.
/// </summary>
/// <param name="Labels">Extra label (or tag) names per kind, matched without regard to case.</param>
/// <param name="ItemTypes">Work-item types per kind, matched without regard to case.</param>
/// <param name="Command">Whether <c>/factory &lt;kind&gt;</c> on a ticket with no kind starts a run of that kind.</param>
public sealed record TriggerRules(
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Labels = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ItemTypes = null,
    bool Command = false)
{
    /// <summary>The kinds a ticket can start, from the one that wins when a ticket carries several to the one that loses: an incident is never built, and a plan is split, not built.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["incident", "plan", "feature", "bug", "docs"];

    public static readonly TriggerRules Default = new();

    /// <summary>The kind of workflow the ticket asks for, or null when it asks for none.</summary>
    public string? KindOf(RepoEvent ticket)
    {
        foreach (var kind in Kinds)
        {
            if (ticket.HasLabel(kind) || (Labels?.GetValueOrDefault(kind) is { } labels && labels.Any(ticket.HasLabel)))
            {
                return kind;
            }
            if (ticket.ItemType is { Length: > 0 } type && ItemTypes?.GetValueOrDefault(kind) is { } types
                && types.Any(known => string.Equals(known, type, StringComparison.OrdinalIgnoreCase)))
            {
                return kind;
            }
        }
        return null;
    }

    /// <summary>
    /// Reads a host's triggers: <c>{"labels":{"feature":["enhancement"]},"item_types":{"bug":["Bug"]},"command":true}</c>. A kind must be one of <see cref="Kinds"/>; each list has at most
    /// 20 names of at most 50 characters. A setting that cannot be used stops the start with a message that names where it is.
    /// </summary>
    public static TriggerRules Read(JsonElement element, string where)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
        {
            return Default;
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{where}: \"triggers\" must be an object.");
        }
        Dictionary<string, IReadOnlyList<string>>? labels = null, itemTypes = null;
        var command = false;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "labels":
                    labels = Lists(property.Value, "labels", where);
                    break;
                case "item_types":
                    itemTypes = Lists(property.Value, "item_types", where);
                    break;
                case "command" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    command = property.Value.GetBoolean();
                    break;
                default:
                    throw new InvalidOperationException($"{where}: \"triggers\" has no setting \"{(property.Name.Length > 30 ? property.Name[..30] : property.Name)}\"; the settings are labels, item_types and command (true or false).");
            }
        }
        return new TriggerRules(labels, itemTypes, command);
    }

    private static Dictionary<string, IReadOnlyList<string>> Lists(JsonElement element, string setting, string where)
    {
        var lists = new Dictionary<string, IReadOnlyList<string>>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{where}: triggers.{setting} must be an object with a list of names per kind ({string.Join(", ", Kinds)}).");
        }
        foreach (var kind in element.EnumerateObject())
        {
            if (!Kinds.Contains(kind.Name) || kind.Value.ValueKind != JsonValueKind.Array || kind.Value.GetArrayLength() > 20)
            {
                throw new InvalidOperationException($"{where}: triggers.{setting} may list at most 20 names for each of {string.Join(", ", Kinds)}.");
            }
            var names = new List<string>();
            foreach (var name in kind.Value.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String || name.GetString()?.Trim() is not { Length: > 0 and <= 50 } text)
                {
                    throw new InvalidOperationException($"{where}: triggers.{setting}.{kind.Name} must be a list of names of at most 50 characters.");
                }
                names.Add(text);
            }
            lists[kind.Name] = names;
        }
        return lists;
    }
}
