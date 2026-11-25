using System.Collections.Concurrent;

namespace AiSdlcFactory.Controller;

/// <summary>An answer kept in memory for a while, by a key. A lifetime of 0 keeps nothing.</summary>
public sealed class TimedCache<T>(TimeSpan lifetime, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> clock = now ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, (T Value, DateTimeOffset At)> items = new();

    public bool TryGet(string key, out T value)
    {
        value = default!;
        if (lifetime <= TimeSpan.Zero || !items.TryGetValue(key.ToLowerInvariant(), out var kept) || clock() - kept.At >= lifetime)
        {
            return false;
        }
        value = kept.Value;
        return true;
    }

    public void Keep(string key, T value)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            return;
        }
        var at = clock();
        items[key.ToLowerInvariant()] = (value, at);
        foreach (var old in items.Where(pair => at - pair.Value.At >= lifetime).Select(pair => pair.Key).ToList())
        {
            items.TryRemove(old, out _);
        }
    }
}

/// <summary>
/// What the git host said a person may do on a repository, kept in memory for a while (<c>FACTORY_PERMISSION_CACHE_SECONDS</c>) so a burst of commands does not ask the host again and again.
/// Only a clear answer is kept: a host that could not say, or failed, is asked again next time. A person whose access was just taken away still passes until the answer expires, which is the
/// price of not asking; a time of 0 switches the memory off.
/// </summary>
public sealed class PermissionCache(TimeSpan lifetime, Func<DateTimeOffset>? now = null)
{
    private readonly TimedCache<HostPermission> answers = new(lifetime, now);

    private static string KeyOf(string owner, string repo, string login) => $"{owner}/{repo}|{login}";

    public bool TryGet(string owner, string repo, string login, out HostPermission permission) => answers.TryGet(KeyOf(owner, repo, login), out permission);

    public void Keep(string owner, string repo, string login, HostPermission permission)
    {
        if (permission != HostPermission.Unknown)
        {
            answers.Keep(KeyOf(owner, repo, login), permission);
        }
    }
}

/// <summary>Whether a person belongs to a team, kept for as long as the permissions are kept. Only a clear answer is kept: a host that could not say is asked again next time.</summary>
public sealed class TeamCache(TimeSpan lifetime, Func<DateTimeOffset>? now = null)
{
    private readonly TimedCache<bool> answers = new(lifetime, now);

    private static string KeyOf(string host, string team, string login) => $"{host}|{team}|{login}";

    public bool TryGet(string host, string team, string login, out bool member) => answers.TryGet(KeyOf(host, team, login), out member);

    public void Keep(string host, string team, string login, bool member) => answers.Keep(KeyOf(host, team, login), member);
}

/// <summary>What a repository's project file says about who may steer the factory, for as long as the permissions are kept: one read of the file serves a burst of commands.</summary>
public sealed class ProjectRuleCache(TimeSpan lifetime, Func<DateTimeOffset>? now = null)
{
    private readonly TimedCache<ProjectCommandRule> rules = new(lifetime, now);

    public bool TryGet(string owner, string repo, out ProjectCommandRule rule) => rules.TryGet($"{owner}/{repo}", out rule!);

    public void Keep(string owner, string repo, ProjectCommandRule rule) => rules.Keep($"{owner}/{repo}", rule);
}
