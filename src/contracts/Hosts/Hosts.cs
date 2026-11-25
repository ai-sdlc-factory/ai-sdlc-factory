using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSdlcFactory.Contracts;

/// <summary>
/// How the factory names a repository. A host called <see cref="DefaultHost"/> (the name the factory's first Forgejo had) keeps plain owners (<c>acme</c>), so every id, folder
/// and log line that exists today is unchanged. An owner on any other host is qualified with the host's name
/// (<c>github:acme</c>), and because the owner is part of every key (workflow ids, workspace folders, the usage log), two hosts can never share a run, a checkout or a count.
/// The qualifier is taken off again where a host is actually spoken to.
/// </summary>
public static partial class RepoNames
{
    /// <summary>The one host name whose owners stay plain. Nothing else about the name is special: it needs no entry in <c>FACTORY_HOSTS</c>, and a factory without it names every owner with its host.</summary>
    public const string DefaultHost = "forgejo";

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,30}\z")]
    private static partial Regex HostNamePattern();

    /// <summary>A host's name: lower case letters, digits and <c>-</c>, starting with a letter, at most 31 characters. It is the end of the webhook route and the qualifier of the owners.</summary>
    public static bool IsHostName(string name) => HostNamePattern().IsMatch(name);

    /// <summary>The owner as the factory names it: plain on the host called <c>forgejo</c>, <c>host:owner</c> elsewhere.</summary>
    public static string Qualify(string host, string owner) =>
        string.Equals(host, DefaultHost, StringComparison.Ordinal) ? owner : $"{host}:{owner}";

    /// <summary>The host and the plain owner of a qualified owner. An owner without a qualifier is the host called forgejo's.</summary>
    public static (string Host, string Owner) Split(string qualifiedOwner) =>
        qualifiedOwner.IndexOf(':') is var colon and > 0 ? (qualifiedOwner[..colon], qualifiedOwner[(colon + 1)..]) : (DefaultHost, qualifiedOwner);
}

/// <summary>
/// One git host as <c>FACTORY_HOSTS</c> describes it. The API reads the name, the kind and the webhook secret; the controller reads the name, the kind, the address, the bot's
/// token and the bot's login; the alert intake reads the name, the address and the token.
/// </summary>
public sealed record HostDefinition(string Name, string Type, string Url, string Token, string BotLogin, string WebhookSecret, TriggerRules? Triggers = null, HostCommandRules? Commands = null, HostStartRules? Start = null);

public static class HostsSetting
{
    public const string Variable = "FACTORY_HOSTS";

    private const string Example = """[{"name":"forgejo","type":"forgejo","url":"http://forgejo:3000","token":"...","bot_login":"factory-bot","webhook_secret":"<32+ characters>"}]""";

    /// <summary>
    /// Reads the <c>FACTORY_HOSTS</c> setting, the one place that says which git hosts the factory serves: a JSON array of
    /// <c>{"name","type","url","token","bot_login","webhook_secret","triggers","commands","repositories"}</c> (<c>type</c> defaults to <c>forgejo</c>, <c>bot_login</c> to <c>factory-bot</c>; <c>name</c> and
    /// <c>url</c> are required; <c>webhook_secret</c> is required where <paramref name="needWebhookSecret"/> is set, which is the API; <c>triggers</c> is optional, see <see cref="TriggerRules"/>; <c>commands</c> and <c>repositories</c> are optional, see <see cref="CommandRule"/>).
    /// The first host is the one a route of the alert intake uses when it names none. At least one host is needed. A setting that cannot be used stops the start with a message
    /// that names the host and the problem, never a secret.
    /// </summary>
    public static IReadOnlyList<HostDefinition> Parse(string? json, bool needWebhookSecret = false)
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
            var hosts = new List<HostDefinition>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var name = Text(item, "name") ?? "";
                if (!RepoNames.IsHostName(name) || hosts.Any(host => host.Name == name))
                {
                    throw new InvalidOperationException(
                        $"{Variable}: every host needs a name of its own (lower case letters, digits and -, starting with a letter, at most 31 characters); \"{Short(name)}\" cannot be used.");
                }
                var type = Text(item, "type") ?? WebhookAdapters.All[0].Type;
                if (WebhookAdapters.Find(type) is null)
                {
                    throw new InvalidOperationException($"{Variable}: host \"{name}\" has the type \"{Short(type)}\", which this factory has no adapter for ({string.Join(", ", WebhookAdapters.All.Select(adapter => adapter.Type))}).");
                }
                var url = Text(item, "url") ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
                {
                    throw new InvalidOperationException($"{Variable}: host \"{name}\" needs a \"url\" that is an http or https address.");
                }
                var secret = Text(item, "webhook_secret") ?? "";
                if (needWebhookSecret && secret.Length < 32)
                {
                    throw new InvalidOperationException($"{Variable}: host \"{name}\" needs a \"webhook_secret\" of at least 32 characters.");
                }
                var triggers = item.TryGetProperty("triggers", out var triggersValue) ? TriggerRules.Read(triggersValue, $"{Variable}: host \"{name}\"") : TriggerRules.Default;
                var commands = HostCommandRules.Read(item, $"{Variable}: host \"{name}\"");
                var start = HostStartRules.Read(item, $"{Variable}: host \"{name}\"");
                hosts.Add(new HostDefinition(name, type, url.TrimEnd('/'), Text(item, "token") ?? "", Text(item, "bot_login") is { Length: > 0 } login ? login : "factory-bot", secret, triggers, commands, start));
            }
            return hosts;
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static string Short(string value) => value.Length > 40 ? value[..40] + "..." : value;
}
