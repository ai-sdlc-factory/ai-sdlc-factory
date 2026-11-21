using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFoundry.AlertIntake;

/// <summary>Where the tickets of the alerts that match <see cref="Match"/> go, and the labels they get.</summary>
/// <remarks><see cref="Host"/> is the name of the git host the ticket opens on (one of <c>ALERT_INTAKE_HOSTS</c>).</remarks>
public sealed record Route(IReadOnlyDictionary<string, string> Match, string Host, string Owner, string Repo, IReadOnlyList<string> Labels, IReadOnlyList<EvidenceQuery>? Evidence = null);

/// <summary>A log query the operator wants run when a ticket is opened, and what to call its result. It comes from the operator's file, never from an alert.</summary>
public sealed record EvidenceQuery(string Title, string Query);

/// <summary>
/// The operator's list of which alert goes to which repository. It is a file the operator owns: an alert can never choose its own repository,
/// so a monitoring rule (or something that writes to it) cannot open tickets anywhere else. The first matching route wins.
/// </summary>
public sealed partial class RouteTable(IReadOnlyList<Route> routes)
{
    public const int MaxRoutes = 50;

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,100}/[A-Za-z0-9._-]{1,100}\z")]
    private static partial Regex Repository();

    [GeneratedRegex(@"\A[A-Za-z0-9 ._:/-]{1,50}\z")]
    private static partial Regex LabelName();

    public int Count => routes.Count;

    public Route? Find(IReadOnlyDictionary<string, string> labels) =>
        routes.FirstOrDefault(route => route.Match.All(pair => labels.TryGetValue(pair.Key, out var value) && value == pair.Value));

    public static RouteTable Load(string path, IReadOnlyList<string>? knownHosts = null)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The routes file {path} cannot be read: {error.Message}");
        }
        return Parse(text, knownHosts);
    }

    /// <param name="knownHosts">The names of the hosts a route may name with <c>host</c>, in the order of <c>ALERT_INTAKE_HOSTS</c>; the first is the host of a route that names none. Null means a single host called forgejo.</param>
    public static RouteTable Parse(string json, IReadOnlyList<string>? knownHosts = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The routes file is not valid JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("routes", out var list)
                || list.ValueKind != JsonValueKind.Array
                || list.GetArrayLength() > MaxRoutes)
            {
                throw new InvalidOperationException($"The routes file must be an object with \"routes\": an array of at most {MaxRoutes} routes.");
            }

            var routes = new List<Route>();
            foreach (var item in list.EnumerateArray())
            {
                routes.Add(ReadRoute(item, routes.Count + 1, knownHosts is { Count: > 0 } ? knownHosts : [HostList.Forgejo]));
            }
            return new RouteTable(routes);
        }
    }

    private static Route ReadRoute(JsonElement item, int number, IReadOnlyList<string> knownHosts)
    {
        var repository = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.String ? repo.GetString() : null;
        if (repository is null || !Repository().IsMatch(repository))
        {
            throw new InvalidOperationException($"Route {number}: \"repository\" must look like owner/name.");
        }

        // A route may name the git host its tickets go to ("host": "github"); without one it is the first configured host.
        var host = knownHosts[0];
        if (item.TryGetProperty("host", out var hostValue))
        {
            host = hostValue.ValueKind == JsonValueKind.String ? hostValue.GetString() ?? "" : "";
            if (!knownHosts.Contains(host))
            {
                throw new InvalidOperationException($"Route {number}: \"host\" must be the name of a configured git host ({string.Join(", ", knownHosts.Order(StringComparer.Ordinal))}).");
            }
        }

        var match = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!item.TryGetProperty("match", out var matchValue) || matchValue.ValueKind != JsonValueKind.Object || matchValue.EnumerateObject().Count() is 0 or > 10)
        {
            throw new InvalidOperationException($"Route {number}: \"match\" must be an object of 1 to 10 alert labels, such as {{\"service\": \"weather\"}}.");
        }
        foreach (var pair in matchValue.EnumerateObject())
        {
            if (pair.Value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(pair.Value.GetString()))
            {
                throw new InvalidOperationException($"Route {number}: the value of \"match.{pair.Name}\" must be a non-empty string.");
            }
            match[pair.Name] = pair.Value.GetString()!;
        }

        var labels = new List<string>();
        if (item.TryGetProperty("labels", out var labelValue))
        {
            if (labelValue.ValueKind != JsonValueKind.Array || labelValue.GetArrayLength() > 5)
            {
                throw new InvalidOperationException($"Route {number}: \"labels\" must be an array of at most 5 ticket labels.");
            }
            foreach (var label in labelValue.EnumerateArray())
            {
                var name = label.ValueKind == JsonValueKind.String ? label.GetString() : null;
                if (name is null || !LabelName().IsMatch(name))
                {
                    throw new InvalidOperationException($"Route {number}: every label must be 1 to 50 plain characters.");
                }
                labels.Add(name);
            }
        }

        var evidence = new List<EvidenceQuery>();
        if (item.TryGetProperty("evidence", out var evidenceValue))
        {
            if (evidenceValue.ValueKind != JsonValueKind.Array || evidenceValue.GetArrayLength() > 3)
            {
                throw new InvalidOperationException($"Route {number}: \"evidence\" must be an array of at most 3 log queries.");
            }
            foreach (var query in evidenceValue.EnumerateArray())
            {
                var title = query.ValueKind == JsonValueKind.Object && query.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var text = query.ValueKind == JsonValueKind.Object && query.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
                if (string.IsNullOrWhiteSpace(title) || title.Length > 80 || string.IsNullOrWhiteSpace(text) || text.Length > 300)
                {
                    throw new InvalidOperationException($"Route {number}: every evidence query needs a \"title\" (80 characters at most) and a \"query\" (300 characters at most).");
                }
                evidence.Add(new EvidenceQuery(title, text));
            }
        }

        var parts = repository.Split('/');
        return new Route(match, host, parts[0], parts[1], labels, evidence);
    }
}
