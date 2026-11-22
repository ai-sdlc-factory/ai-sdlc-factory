using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSdlcFactory.EgressProxy;

/// <summary>A named list of hosts a sandbox may reach. Audit profiles allow everything and log what they would have denied.</summary>
public sealed record EgressProfile(string Name, IReadOnlyList<string> Hosts, bool Audit);

public sealed record EgressConfig(IReadOnlySet<int> Ports, IReadOnlyDictionary<string, EgressProfile> Profiles)
{
    /// <summary>
    /// The same ports and profiles with every profile in audit mode: any host is allowed and logged. The other
    /// rules (HTTPS ports, no IP addresses, no private addresses) stay in force.
    /// </summary>
    public EgressConfig WithAuditMode() =>
        this with { Profiles = Profiles.ToDictionary(pair => pair.Key, pair => pair.Value with { Audit = true }) };
}

/// <summary>
/// Reads the factory\'s trusted allowlist file (src/egress-proxy/profiles.json). Nothing a repository or an agent
/// says can add a host: only a person editing this file can.
/// </summary>
public static partial class EgressConfigParser
{
    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,30}\z")]
    private static partial Regex ProfileName();

    // A host name with at least two labels and a last label that starts with a letter (so never an IP address).
    // A leading "*." means any subdomain, but not the bare domain.
    [GeneratedRegex(@"\A(\*\.)?(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?\z")]
    private static partial Regex HostPattern();

    public static bool TryParse(string? json, [NotNullWhen(true)] out EgressConfig? config, out string error)
    {
        config = null;
        error = "";
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json ?? "", new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            error = "the file is not valid JSON";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the file must contain a JSON object";
                return false;
            }

            var ports = new HashSet<int>();
            if (!root.TryGetProperty("ports", out var portsValue) || portsValue.ValueKind != JsonValueKind.Array
                || portsValue.GetArrayLength() is 0 or > 8)
            {
                error = "\"ports\" must be an array of 1 to 8 port numbers, such as [443]";
                return false;
            }
            foreach (var port in portsValue.EnumerateArray())
            {
                if (port.ValueKind != JsonValueKind.Number || !port.TryGetInt32(out var number) || number is < 1 or > 65535)
                {
                    error = "\"ports\" must contain port numbers between 1 and 65535";
                    return false;
                }
                ports.Add(number);
            }

            if (!root.TryGetProperty("profiles", out var profilesValue) || profilesValue.ValueKind != JsonValueKind.Object)
            {
                error = "\"profiles\" must be an object of named profiles";
                return false;
            }

            var own = new Dictionary<string, (List<string> Hosts, List<string> Include, bool Audit)>();
            foreach (var entry in profilesValue.EnumerateObject())
            {
                if (!ProfileName().IsMatch(entry.Name))
                {
                    error = $"profile name \"{entry.Name}\" must be lower case letters, digits and -";
                    return false;
                }
                if (!ReadProfile(entry.Name, entry.Value, out var hosts, out var include, out var audit, out error))
                {
                    return false;
                }
                own[entry.Name] = (hosts, include, audit);
            }
            if (own.Count == 0)
            {
                error = "define at least one profile";
                return false;
            }

            var profiles = new Dictionary<string, EgressProfile>();
            foreach (var (name, (hosts, include, audit)) in own)
            {
                var all = new List<string>(hosts);
                foreach (var included in include)
                {
                    // One level only: an included profile must stand on its own, which rules out cycles.
                    if (!own.TryGetValue(included, out var source) || source.Include.Count > 0 || included == name)
                    {
                        error = $"profile \"{name}\" includes \"{included}\", which must be another profile that does not include others";
                        return false;
                    }
                    all.AddRange(source.Hosts);
                }
                profiles[name] = new EgressProfile(name, all.Distinct(StringComparer.Ordinal).ToList(), audit);
            }
            config = new EgressConfig(ports, profiles);
            return true;
        }
    }

    private static bool ReadProfile(
        string name, JsonElement value, out List<string> hosts, out List<string> include, out bool audit, out string error)
    {
        hosts = [];
        include = [];
        audit = false;
        error = "";
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = $"profile \"{name}\" must be an object";
            return false;
        }
        if (value.TryGetProperty("mode", out var mode))
        {
            if (mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("enforce" or "audit"))
            {
                error = $"profile \"{name}\": \"mode\" must be \"enforce\" or \"audit\"";
                return false;
            }
            audit = mode.GetString() == "audit";
        }
        if (value.TryGetProperty("hosts", out var hostsValue))
        {
            if (hostsValue.ValueKind != JsonValueKind.Array || hostsValue.GetArrayLength() > 200)
            {
                error = $"profile \"{name}\": \"hosts\" must be an array of at most 200 host names";
                return false;
            }
            foreach (var host in hostsValue.EnumerateArray())
            {
                var text = host.ValueKind == JsonValueKind.String ? host.GetString()!.Trim().ToLowerInvariant() : null;
                if (text is null || text.Length > 253 || !HostPattern().IsMatch(text))
                {
                    error = $"profile \"{name}\": \"{(host.ValueKind == JsonValueKind.String ? host.GetString() : host.ToString())}\" is not a host name like example.com or *.example.com";
                    return false;
                }
                hosts.Add(text);
            }
        }
        if (value.TryGetProperty("include", out var includeValue))
        {
            if (includeValue.ValueKind != JsonValueKind.Array || includeValue.GetArrayLength() > 8
                || includeValue.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                error = $"profile \"{name}\": \"include\" must be an array of profile names";
                return false;
            }
            include.AddRange(includeValue.EnumerateArray().Select(item => item.GetString()!));
        }
        return true;
    }
}
