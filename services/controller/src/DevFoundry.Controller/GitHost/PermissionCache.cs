using System.Collections.Concurrent;

namespace DevFoundry.Controller;

/// <summary>
/// What the git host said a person may do on a repository, kept in memory for a while (<c>FACTORY_PERMISSION_CACHE_SECONDS</c>) so a burst of commands does not ask the host again and again.
/// Only a clear answer is kept: a host that could not say, or failed, is asked again next time. A person whose access was just taken away still passes until the answer expires, which is the
/// price of not asking; a time of 0 switches the memory off.
/// </summary>
public sealed class PermissionCache(TimeSpan lifetime, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> clock = now ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, (HostPermission Permission, DateTimeOffset At)> answers = new();

    private static string KeyOf(string owner, string repo, string login) => $"{owner}/{repo}|{login}".ToLowerInvariant();

    public bool TryGet(string owner, string repo, string login, out HostPermission permission)
    {
        permission = HostPermission.Unknown;
        if (lifetime <= TimeSpan.Zero || !answers.TryGetValue(KeyOf(owner, repo, login), out var kept) || clock() - kept.At >= lifetime)
        {
            return false;
        }
        permission = kept.Permission;
        return true;
    }

    public void Keep(string owner, string repo, string login, HostPermission permission)
    {
        if (lifetime <= TimeSpan.Zero || permission == HostPermission.Unknown)
        {
            return;
        }
        var at = clock();
        answers[KeyOf(owner, repo, login)] = (permission, at);
        foreach (var old in answers.Where(pair => at - pair.Value.At >= lifetime).Select(pair => pair.Key).ToList())
        {
            answers.TryRemove(old, out _);
        }
    }
}
