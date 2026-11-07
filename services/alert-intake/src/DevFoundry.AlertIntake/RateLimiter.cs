namespace DevFoundry.AlertIntake;

/// <summary>At most <c>limit</c> new tickets in any hour, so a flapping rule or a broken system cannot flood the repository with tickets.</summary>
public sealed class RateLimiter(int limit, TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly Queue<DateTimeOffset> taken = new();
    private readonly object gate = new();

    public bool TryAcquire()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            while (taken.Count > 0 && now - taken.Peek() >= Window)
            {
                taken.Dequeue();
            }
            if (taken.Count >= limit)
            {
                return false;
            }
            taken.Enqueue(now);
            return true;
        }
    }
}
