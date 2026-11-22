using System.Text.Json;

namespace AiSdlcFactory.EgressProxy;

public sealed record AccessEntry(
    DateTimeOffset Time, string Profile, string Client, string Host, int Port,
    string Decision, string Reason, long BytesUp, long BytesDown, long Milliseconds);

/// <summary>Every decision is recorded: this is how you see what a sandbox tried to reach.</summary>
public interface IAccessLog
{
    void Write(AccessEntry entry);
}

public sealed class ConsoleAccessLog : IAccessLog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly object gate = new();

    public void Write(AccessEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, Options);
        lock (gate)
        {
            Console.Out.WriteLine(line);
        }
    }
}

/// <summary>
/// Keeps every refusal in a file (one JSON line each, the same fields as the console log) so that something other than the console can see which hosts sandboxes tried
/// to reach and were refused: the factory's weekly report reads it. Only refusals are written, which keeps it small, and it stops growing at <see cref="MaxBytes"/>. A file that cannot
/// be written is reported once on the console and otherwise ignored: the proxy's job is to proxy, never to fail because of its log.
/// </summary>
public sealed class DeniedLogFile(string path, long maxBytes = FileAccessLog.MaxBytes) : IAccessLog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly object gate = new();
    private bool complained;

    public void Write(AccessEntry entry)
    {
        if (entry.Decision != "deny")
        {
            return;
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Options) + "\n");
        lock (gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length + bytes.Length > maxBytes)
                {
                    Complain($"the refusal log {path} has reached {maxBytes} bytes, so no more refusals are written to it (move or delete it to start again)");
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Complain($"the refusal log {path} cannot be written ({error.Message}); refusals are still logged to the console");
            }
        }
    }

    private void Complain(string message)
    {
        if (!complained)
        {
            complained = true;
            Console.Error.WriteLine("warning: " + message);
        }
    }
}

public static class FileAccessLog
{
    public const long MaxBytes = 10L * 1024 * 1024;
}

/// <summary>Every entry goes to each log in turn.</summary>
public sealed class TeeAccessLog(params IAccessLog[] logs) : IAccessLog
{
    public void Write(AccessEntry entry)
    {
        foreach (var log in logs)
        {
            log.Write(entry);
        }
    }
}

