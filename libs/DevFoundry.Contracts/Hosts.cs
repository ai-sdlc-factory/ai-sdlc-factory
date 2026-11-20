using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFoundry.Contracts;

/// <summary>
/// How the factory names a repository that lives on a host other than the default one. The default host (<see cref="DefaultHost"/>, the Forgejo the factory started with)
/// keeps plain owners (<c>acme</c>), so every id, folder and log line that exists today is unchanged. An owner on any other host is qualified with the host's name
/// (<c>codeberg:acme</c>), and because the owner is part of every key (workflow ids, workspace folders, the usage log), two hosts can never share a run, a checkout or a count.
/// The qualifier is taken off again where a host is actually spoken to.
/// </summary>
public static partial class RepoNames
{
    public const string DefaultHost = "forgejo";

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,30}\z")]
    private static partial Regex HostNamePattern();

    /// <summary>A host's name: lower case letters, digits and <c>-</c>, starting with a letter, at most 31 characters. It is the end of the webhook route and the qualifier of the owners.</summary>
    public static bool IsHostName(string name) => HostNamePattern().IsMatch(name);

    /// <summary>The owner as the factory names it: plain on the default host, <c>host:owner</c> elsewhere.</summary>
    public static string Qualify(string host, string owner) =>
        string.Equals(host, DefaultHost, StringComparison.Ordinal) ? owner : $"{host}:{owner}";

    /// <summary>The host and the plain owner of a qualified owner. An owner without a qualifier is the default host's.</summary>
    public static (string Host, string Owner) Split(string qualifiedOwner) =>
        qualifiedOwner.IndexOf(':') is var colon and > 0 ? (qualifiedOwner[..colon], qualifiedOwner[(colon + 1)..]) : (DefaultHost, qualifiedOwner);
}

/// <summary>
/// One git host besides the default one, as <c>FACTORY_EXTRA_HOSTS</c> describes it. The API reads the name, the kind and the webhook secret; the controller reads the name, the kind,
/// the address, the bot's token and the bot's login.
/// </summary>
public sealed record HostDefinition(string Name, string Type, string Url, string Token, string BotLogin, string WebhookSecret);

public static class ExtraHostsSetting
{
    /// <summary>
    /// Reads the <c>FACTORY_EXTRA_HOSTS</c> setting: a JSON array of <c>{"name","type","url","token","bot_login","webhook_secret"}</c> (<c>type</c> defaults to <c>forgejo</c>,
    /// <c>bot_login</c> to <c>factory-bot</c>; <c>name</c> and <c>url</c> are required). Empty means no extra host. A setting that cannot be used stops the start with a message
    /// that names the host and the problem, never a secret.
    /// </summary>
    public static IReadOnlyList<HostDefinition> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("FACTORY_EXTRA_HOSTS must be a JSON array of hosts.");
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("FACTORY_EXTRA_HOSTS must be a JSON array of hosts.");
            }
            var hosts = new List<HostDefinition>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var name = Text(item, "name") ?? "";
                if (!RepoNames.IsHostName(name) || name == RepoNames.DefaultHost || hosts.Any(host => host.Name == name))
                {
                    throw new InvalidOperationException(
                        $"FACTORY_EXTRA_HOSTS: every host needs a name of its own (lower case letters, digits and -, starting with a letter, at most 31 characters; \"{RepoNames.DefaultHost}\" is the default host's); \"{Short(name)}\" cannot be used.");
                }
                var type = Text(item, "type") ?? WebhookAdapters.All[0].Type;
                if (WebhookAdapters.Find(type) is null)
                {
                    throw new InvalidOperationException($"FACTORY_EXTRA_HOSTS: host \"{name}\" has the type \"{Short(type)}\", which this factory has no adapter for ({string.Join(", ", WebhookAdapters.All.Select(adapter => adapter.Type))}).");
                }
                var url = Text(item, "url") ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
                {
                    throw new InvalidOperationException($"FACTORY_EXTRA_HOSTS: host \"{name}\" needs a \"url\" that is an http or https address.");
                }
                var secret = Text(item, "webhook_secret") ?? "";
                if (secret.Length < 32)
                {
                    throw new InvalidOperationException($"FACTORY_EXTRA_HOSTS: host \"{name}\" needs a \"webhook_secret\" of at least 32 characters.");
                }
                hosts.Add(new HostDefinition(name, type, url.TrimEnd('/'), Text(item, "token") ?? "", Text(item, "bot_login") is { Length: > 0 } login ? login : "factory-bot", secret));
            }
            return hosts;
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static string Short(string value) => value.Length > 40 ? value[..40] + "..." : value;
}
