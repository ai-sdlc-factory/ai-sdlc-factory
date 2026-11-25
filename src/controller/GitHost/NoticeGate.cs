namespace AiSdlcFactory.Controller;

/// <summary>
/// Makes "look at the ticket's comments, then post the notice if it is not there" one step per ticket. Events that reach the factory together (a ticket created with three labels sends four) are
/// handled at the same time, and without this each would find the notice missing and all of them would post it. There is one controller, so a lock in memory is enough (a second controller is already
/// a documented limitation); a few locks shared by tickets keep it small, and two tickets that share one only wait for each other for the length of one comment.
/// </summary>
public static class NoticeGate
{
    private static readonly SemaphoreSlim[] Locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>Waits for this ticket's turn; dispose what comes back to let the next one in.</summary>
    public static async Task<IDisposable> EnterAsync(string owner, string repo, int issueNumber, CancellationToken cancellationToken)
    {
        var gate = Locks[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode($"{owner}/{repo}#{issueNumber}") % (uint)Locks.Length)];
        await gate.WaitAsync(cancellationToken);
        return new Turn(gate);
    }

    private sealed class Turn(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
