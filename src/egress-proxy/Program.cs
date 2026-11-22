using System.Net;
using System.Net.Sockets;
using AiSdlcFactory.EgressProxy;

var port = int.TryParse(Environment.GetEnvironmentVariable("EGRESS_PORT"), out var configured) ? configured : 3128;

// Container healthcheck: the runtime image has no curl, so the app probes itself. A connection that closes
// without sending anything is not logged.
if (args.Contains("--healthcheck"))
{
    try
    {
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));
        return 0;
    }
    catch (Exception error) when (error is SocketException or TimeoutException)
    {
        return 1;
    }
}

var file = Environment.GetEnvironmentVariable("EGRESS_PROFILES_FILE");
if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
{
    Console.Error.WriteLine("EGRESS_PROFILES_FILE must name the allowlist file.");
    return 2;
}
if (!EgressConfigParser.TryParse(File.ReadAllText(file), out var config, out var configError))
{
    Console.Error.WriteLine($"{file} cannot be used: {configError}");
    return 2;
}
if (!EgressPolicy.TryParseListenMap(Environment.GetEnvironmentVariable("EGRESS_LISTEN_MAP"), out var listenMap, out var mapError))
{
    Console.Error.WriteLine($"EGRESS_LISTEN_MAP cannot be used: {mapError}");
    return 2;
}
// EGRESS_MODE: enforce (default) uses the allowlist; audit allows every HTTPS host and logs it. "open" is a
// launcher setting that bypasses this proxy altogether, so here it changes nothing.
var mode = (Environment.GetEnvironmentVariable("EGRESS_MODE") ?? "enforce").Trim().ToLowerInvariant();
switch (mode)
{
    case "" or "enforce" or "open":
        break;
    case "audit":
        config = config.WithAuditMode();
        break;
    default:
        Console.Error.WriteLine($"EGRESS_MODE \"{mode}\" is not one of enforce, audit, open.");
        return 2;
}

foreach (var profile in listenMap.Values.Distinct())
{
    if (!config.Profiles.ContainsKey(profile))
    {
        Console.Error.WriteLine($"EGRESS_LISTEN_MAP names profile \"{profile}\", which {file} does not define.");
        return 2;
    }
}

foreach (var profile in config.Profiles.Values)
{
    Console.WriteLine($"profile {profile.Name}: {profile.Hosts.Count} hosts, ports {string.Join(",", config.Ports)}"
        + (profile.Audit ? " -- AUDIT MODE: every host is allowed and only logged" : ""));
}
if (mode == "audit")
{
    Console.WriteLine("WARNING: EGRESS_MODE=audit. The allowlist is NOT being enforced; every HTTPS host is reachable.");
}
foreach (var unused in config.Profiles.Keys.Except(listenMap.Values))
{
    Console.WriteLine($"note: profile {unused} has no address in EGRESS_LISTEN_MAP, so no sandbox can use it");
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

// EGRESS_DENIED_LOG: a file that every refusal is also written to (optional), so that the factory's weekly report can read it.
IAccessLog accessLog = new ConsoleAccessLog();
if (Environment.GetEnvironmentVariable("EGRESS_DENIED_LOG") is { Length: > 0 } deniedLog)
{
    accessLog = new TeeAccessLog(accessLog, new DeniedLogFile(deniedLog));
    Console.WriteLine($"refusals are also written to {deniedLog}");
}

var server = new ProxyServer(new EgressPolicy(config, listenMap), new SystemResolver(), accessLog, new ProxyOptions());
Console.WriteLine($"egress proxy listening on port {port}");
await server.RunAsync(new TcpListener(IPAddress.Any, port), stop.Token);
return 0;
