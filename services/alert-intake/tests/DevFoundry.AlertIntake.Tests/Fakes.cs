using System.Text.Json;
using DevFoundry.AlertIntake;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevFoundry.AlertIntake.Tests;

public sealed class FakeForgejo : IForgejo
{
    public sealed record Ticket(string Owner, string Repo, int Number, string Title, string Body, IReadOnlyList<string> Labels, bool Open = true);

    public List<Ticket> Tickets { get; } = [];
    public List<(string Owner, string Repo, int Number, string Body)> Comments { get; } = [];
    public Exception? Failure { get; set; }

    public Task<int?> FindOpenTicketAsync(string owner, string repo, string key, CancellationToken cancellationToken)
    {
        Fail();
        var marker = TicketText.Marker(key);
        return Task.FromResult(Tickets.Where(t => t.Open && t.Owner == owner && t.Repo == repo && t.Body.Contains(marker)).Select(t => (int?)t.Number).FirstOrDefault());
    }

    public Task<IssueRef> CreateTicketAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        Fail();
        var number = Tickets.Count + 1;
        Tickets.Add(new Ticket(owner, repo, number, title, body, labels));
        return Task.FromResult(new IssueRef(number, $"http://forgejo/{owner}/{repo}/issues/{number}"));
    }

    public Task CommentAsync(string owner, string repo, int number, string body, CancellationToken cancellationToken)
    {
        Fail();
        Comments.Add((owner, repo, number, body));
        return Task.CompletedTask;
    }

    public void Close(int number) => Tickets[number - 1] = Tickets[number - 1] with { Open = false };

    private void Fail()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

public sealed class FakeLogs : ILogSource
{
    public List<string> Queries { get; } = [];
    public Dictionary<string, string[]> Lines { get; } = [];
    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<string>> RecentLinesAsync(string query, TimeSpan window, int maxLines, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<string>>(Lines.TryGetValue(query, out var lines) ? lines : []);
    }
}

public static class Setup
{
    public const string Token = "intake-test-token-0123456789";

    public const string WeatherRoutes = """{"routes":[{"match":{"service":"weather"},"repository":"valters/weather","labels":["incident"]}]}""";

    public static IntakeSettings Settings(bool dryRun = false, int perHour = 6, int maxBody = 262_144) => new()
    {
        Token = Token, ForgejoToken = "forgejo-token", DryRun = dryRun, MaxTicketsPerHour = perHour, MaxBodyBytes = maxBody,
    };

    public static AlertProcessor Processor(FakeForgejo forgejo, IntakeSettings? settings = null, string routes = WeatherRoutes, TimeProvider? clock = null, ILogSource? logs = null, IntakeMetrics? metrics = null)
    {
        settings ??= Settings();
        var table = RouteTable.Parse(routes);
        return new AlertProcessor(settings, table, forgejo, logs ?? new NoLogs(), new RateLimiter(settings.MaxTicketsPerHour, clock ?? TimeProvider.System),
            metrics ?? new IntakeMetrics(settings, table), NullLogger<AlertProcessor>.Instance);
    }

    /// <summary>An Alertmanager notification (webhook version 4) with the given alerts.</summary>
    public static string Message(params object[] alerts) =>
        JsonSerializer.Serialize(new { version = "4", status = "firing", receiver = "intake", alerts });

    public static object Firing(string name = "WeatherApiDown", string service = "weather", string fingerprint = "a1b2c3d4e5f60718", string summary = "The weather API does not answer",
        string description = "probe failed for 1m", string severity = "critical") =>
        Alert("firing", name, service, fingerprint, summary, description, severity);

    public static object Resolved(string name = "WeatherApiDown", string service = "weather", string fingerprint = "a1b2c3d4e5f60718") =>
        Alert("resolved", name, service, fingerprint, "The weather API does not answer", "", "critical");

    private static object Alert(string status, string name, string service, string fingerprint, string summary, string description, string severity) => new
    {
        status,
        labels = new Dictionary<string, string> { ["alertname"] = name, ["service"] = service, ["severity"] = severity },
        annotations = new Dictionary<string, string> { ["summary"] = summary, ["description"] = description },
        startsAt = "2026-11-07T21:00:00Z",
        endsAt = status == "resolved" ? "2026-11-07T21:10:00Z" : "0001-01-01T00:00:00Z",
        generatorURL = "http://prometheus:9090/graph?g0.expr=probe_success",
        fingerprint,
    };

    public static AlertMessage Parse(string json)
    {
        Assert.True(AlertMessage.TryParse(json, out var message, out var error), error);
        return message!;
    }
}
