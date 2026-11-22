using System.Net;

namespace AiSdlcFactory.EgressProxy;

public sealed record Decision(bool Allowed, string Reason, bool Audit = false);

/// <summary>
/// Decides whether a sandbox may open a tunnel. The sandbox is identified by the proxy address it reached:
/// every profile has its own network and its own address, and a container can only reach the address on
/// its own network, so it cannot ask for a more generous profile.
/// </summary>
public sealed class EgressPolicy(EgressConfig config, IReadOnlyDictionary<IPAddress, string> listenMap)
{
    public EgressConfig Config { get; } = config;

    public string? ProfileFor(IPAddress localAddress) =>
        listenMap.TryGetValue(Normalize(localAddress), out var profile) ? profile : null;

    public Decision Decide(string profileName, string host, int port)
    {
        if (!Config.Profiles.TryGetValue(profileName, out var profile))
        {
            return new Decision(false, $"unknown profile \"{profileName}\"");
        }
        var name = host.TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(name, out _) || name.Contains(':'))
        {
            return new Decision(false, "IP addresses are not allowed; use the host name");
        }
        if (!Config.Ports.Contains(port))
        {
            return new Decision(false, $"port {port} is not allowed");
        }
        if (Matches(profile.Hosts, name))
        {
            return new Decision(true, "on the allowlist");
        }
        return profile.Audit
            ? new Decision(true, "audit mode: not on the allowlist", Audit: true)
            : new Decision(false, $"{name} is not on the allowlist of profile \"{profileName}\"");
    }

    public static bool Matches(IEnumerable<string> allowed, string host)
    {
        foreach (var entry in allowed)
        {
            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                // Any subdomain, but not the bare domain: "*.example.com" does not match "example.com".
                if (host.EndsWith(entry[1..], StringComparison.Ordinal) && host.Length > entry.Length - 1)
                {
                    return true;
                }
            }
            else if (host == entry)
            {
                return true;
            }
        }
        return false;
    }

    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>Parses <c>profile=address,profile=address</c>.</summary>
    public static bool TryParseListenMap(string? value, out Dictionary<IPAddress, string> map, out string error)
    {
        map = [];
        error = "";
        foreach (var pair in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || !IPAddress.TryParse(parts[1], out var address))
            {
                error = $"\"{pair}\" must look like profile=10.0.0.2";
                return false;
            }
            if (!map.TryAdd(Normalize(address), parts[0]))
            {
                error = $"address {parts[1]} is listed twice";
                return false;
            }
        }
        if (map.Count == 0)
        {
            error = "no profile addresses were given";
            return false;
        }
        return true;
    }
}

/// <summary>Addresses a sandbox must never be tunnelled to, whatever name pointed at them.</summary>
public static class AddressRules
{
    public static bool IsPublic(IPAddress address)
    {
        address = EgressPolicy.Normalize(address);
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.IsIPv6Multicast || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal || address.IsIPv6Teredo)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16)
        {
            // fc00::/7 unique local; 64:ff9b::/96 NAT64 can embed any IPv4 address.
            return (bytes[0] & 0xFE) != 0xFC && !(bytes[0] == 0 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B);
        }

        return !(bytes[0] == 0                                        // 0.0.0.0/8
            || bytes[0] == 10                                         // private
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)     // carrier-grade NAT
            || bytes[0] == 127                                        // loopback
            || (bytes[0] == 169 && bytes[1] == 254)                  // link-local, cloud metadata
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)      // private
            || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)   // IETF protocol assignments
            || (bytes[0] == 192 && bytes[1] == 168)                  // private
            || (bytes[0] == 198 && bytes[1] is 18 or 19)             // benchmarking
            || bytes[0] >= 224);                                      // multicast and reserved
    }
}
