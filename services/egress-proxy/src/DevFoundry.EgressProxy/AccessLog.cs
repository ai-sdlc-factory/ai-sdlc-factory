using System.Text.Json;

namespace DevFoundry.EgressProxy;

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
