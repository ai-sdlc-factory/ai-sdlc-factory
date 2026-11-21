using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFoundry.AlertIntake;

/// <summary>A git host the intake may open tickets on: its name (what a route says), the kind it is (<c>forgejo</c> or <c>github</c>), where it is, and the bot's token on it.</summary>
public sealed record IntakeHost(string Name, string Type, string Url, string Token);

public static partial class HostList
{
    public const string Variable = "ALERT_INTAKE_HOSTS";
    public const string Forgejo = "forgejo";
    public const string GitHub = "github";

    private const string Example = """[{"name":"forgejo","type":"forgejo","url":"http://forgejo:3000","token":"..."},{"name":"github","type":"github","url":"https://github.com","token":"..."}]""";

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,30}\z")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Reads <c>ALERT_INTAKE_HOSTS</c>: a JSON array of <c>{"name","type","url","token"}</c> (<c>type</c> defaults to <c>forgejo</c>; the token may be empty during a dry run). At least one host is needed;
    /// the first is the host of a route that names none. A setting that cannot be used stops the start with a message that names the host and never prints a token.
    /// </summary>
    public static IReadOnlyList<IntakeHost> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"{Variable} must list at least one git host, for example {Example}");
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{Variable} must be a JSON array of hosts, for example {Example}");
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                throw new InvalidOperationException($"{Variable} must be a JSON array with at least one host, for example {Example}");
            }
            var hosts = new List<IntakeHost>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var name = Text(item, "name") ?? "";
                if (!NamePattern().IsMatch(name) || hosts.Any(host => host.Name == name))
                {
                    throw new InvalidOperationException($"{Variable}: every host needs a name of its own (lower case letters, digits and -, starting with a letter, at most 31 characters); \"{Short(name)}\" cannot be used.");
                }
                var type = Text(item, "type") ?? Forgejo;
                if (TicketHostKinds.Find(type) is null)
                {
                    throw new InvalidOperationException($"{Variable}: host \"{name}\" has the type \"{Short(type)}\", which the intake has no client for ({string.Join(", ", TicketHostKinds.All.Select(kind => kind.Type))}).");
                }
                var url = Text(item, "url") ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
                {
                    throw new InvalidOperationException($"{Variable}: host \"{name}\" needs a \"url\" that is an http or https address.");
                }
                hosts.Add(new IntakeHost(name, type, url.TrimEnd('/'), Text(item, "token") ?? ""));
            }
            return hosts;
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static string Short(string value) => value.Length > 40 ? value[..40] + "..." : value;
}
