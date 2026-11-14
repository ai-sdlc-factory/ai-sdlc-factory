
namespace DevFoundry.Controller;

/// <summary>A ticket that asks the factory to build a feature, fix a bug, look at an incident or plan a larger request (<see cref="Kind"/>).</summary>
public sealed record WorkRequest(
    string Owner, string Repo, int IssueNumber, string Title, string Body, string Author,
    IReadOnlyList<string>? Labels = null, string Kind = WorkIds.Feature)
{
    public string WorkflowId => WorkIds.For(Owner, Repo, IssueNumber, Kind);

    public bool IsBug => Kind == WorkIds.Bug;

    public bool IsIncident => Kind == WorkIds.Incident;

    public bool IsPlan => Kind == WorkIds.Plan;
}

/// <summary>A human comment on a ticket that has a feature workflow.</summary>
public sealed record HumanReply(string Author, string Body);

/// <summary>
/// The workflow id, which is also the name of the ticket's workspace folder:
/// <c>feature-{owner}-{repo}-{number}-{hash}</c> (<c>bug-...</c> for a bug ticket, <c>plan-...</c> for a plan). The readable part is lowercased, cleaned and shortened so the
/// whole id always fits the workspace name limit (128 characters). The hash is of the exact owner and repository,
/// so repositories whose cleaned names look alike (<c>a-b/c</c> and <c>a/b-c</c>) still get different ids, and so
/// do very long names that had to be cut.
/// </summary>
public static class WorkIds
{
    public const string Feature = "feature";
    public const string Bug = "bug";
    public const string Incident = "incident";
    public const string Plan = "plan";
    public const string Revision = "revision";
    public const string Qa = "qa";
    public const string Update = "update";

    private const int MaxReadable = 80;

    public static string For(string owner, string repo, int issueNumber, string kind = Feature)
    {
        var readable = new string($"{owner}-{repo}".ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '-').ToArray());
        if (readable.Length > MaxReadable)
        {
            readable = readable[..MaxReadable];
        }

        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{owner}/{repo}".ToLowerInvariant()));
        return $"{kind}-{readable}-{issueNumber}-{Convert.ToHexString(digest, 0, 4).ToLowerInvariant()}";
    }
}
