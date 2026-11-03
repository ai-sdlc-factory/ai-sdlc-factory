using System.Text;
using System.Text.RegularExpressions;

namespace DevFoundry.EgressProxy;

public sealed record ConnectRequest(string Host, int Port);

public enum ParseFailure
{
    None,
    NotConnect,
    Malformed,
}

/// <summary>Reads the first line of a proxy request. Only <c>CONNECT host:port</c> is supported.</summary>
public static partial class ConnectRequestParser
{
    [GeneratedRegex(@"\A[A-Za-z0-9.-]{1,253}\z")]
    private static partial Regex HostChars();

    public static ParseFailure TryParse(byte[] head, out ConnectRequest? request)
    {
        request = null;
        var end = Array.IndexOf(head, (byte)'\r');
        var line = Encoding.ASCII.GetString(head, 0, end < 0 ? head.Length : end);
        var parts = line.Split(' ');
        if (parts.Length != 3 || parts[2] is not ("HTTP/1.0" or "HTTP/1.1"))
        {
            return ParseFailure.Malformed;
        }
        if (parts[0] != "CONNECT")
        {
            return ParseFailure.NotConnect;
        }

        var target = parts[1];
        var colon = target.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(target.AsSpan(colon + 1), out var port) || port is < 1 or > 65535)
        {
            return ParseFailure.Malformed;
        }
        var host = target[..colon];
        // IPv6 literals ("[::1]") and anything with odd characters are not host names; the policy refuses
        // IP addresses anyway, but the log and the response must never carry arbitrary client bytes.
        if (host.StartsWith('[') || !HostChars().IsMatch(host))
        {
            return ParseFailure.Malformed;
        }
        request = new ConnectRequest(host, port);
        return ParseFailure.None;
    }
}
