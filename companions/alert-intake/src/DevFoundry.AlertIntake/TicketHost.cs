namespace DevFoundry.AlertIntake;

public sealed record IssueRef(int Number, string Url);

/// <summary>The little of a git host the intake needs: its tickets. One implementation per kind of host; a fake stands in for it in the tests.</summary>
public interface ITicketHost
{
    /// <summary>The number of an open ticket that carries this alert's marker, or null.</summary>
    Task<int?> FindOpenTicketAsync(string owner, string repo, string key, CancellationToken cancellationToken);

    /// <summary>The number of an open ticket that carries this route marker and was opened at or after <paramref name="notBefore"/>, or null.</summary>
    Task<int?> FindRecentTicketAsync(string owner, string repo, string groupMarker, DateTimeOffset notBefore, CancellationToken cancellationToken);

    /// <summary>Adds a hidden marker to a ticket's text, so a later notice for the alert that was added to it finds the ticket by its own marker.</summary>
    Task AddMarkerAsync(string owner, string repo, int number, string marker, CancellationToken cancellationToken);

    Task<IssueRef> CreateTicketAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken);

    Task CommentAsync(string owner, string repo, int number, string body, CancellationToken cancellationToken);
}

/// <summary>The hosts a route may name: the client of the host with this name.</summary>
public interface ITicketHosts
{
    ITicketHost For(string host);
}

public sealed class TicketHosts(IReadOnlyDictionary<string, ITicketHost> hosts) : ITicketHosts
{
    public ITicketHost For(string host) =>
        hosts.TryGetValue(host, out var client) ? client : throw new InvalidOperationException($"No git host named \"{host}\" is configured.");
}
