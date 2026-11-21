using System.Collections.Concurrent;

namespace DevFoundry.AlertIntake;

/// <summary>
/// The tickets the intake opened or added to in the last couple of minutes, kept in memory. A git host may not list a ticket it has just created for a few seconds (GitHub does not),
/// and an alert of the same fingerprint or the same route that arrives in that gap would otherwise open a second ticket. Only what this process itself did is remembered; the host stays
/// the source of truth for everything older.
/// </summary>
public sealed class RecentTickets
{
    public static readonly TimeSpan Remember = TimeSpan.FromMinutes(2);

    public const string AlertKey = "alert";
    public const string RouteGroup = "group";

    private readonly ConcurrentDictionary<string, (int Number, DateTimeOffset At)> items = new();

    private static string KeyOf(string kind, Route route, string key) => $"{route.Host}|{route.Owner}/{route.Repo}|{kind}|{key}";

    public int? Find(string kind, Route route, string key, DateTimeOffset now) =>
        items.TryGetValue(KeyOf(kind, route, key), out var item) && now - item.At < Remember ? item.Number : null;

    public void Add(string kind, Route route, string key, int number, DateTimeOffset now)
    {
        items[KeyOf(kind, route, key)] = (number, now);
        foreach (var old in items.Where(pair => now - pair.Value.At >= Remember).Select(pair => pair.Key).ToList())
        {
            items.TryRemove(old, out _);
        }
    }
}
