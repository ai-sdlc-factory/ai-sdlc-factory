using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Temporalio.Exceptions;

namespace OpenCodeWorker;

public static partial class OpenCodeRunner
{
    private static readonly string[] ArtifactNames = ["hello.py", "test_hello.py"];
    private const int StreamLimit = 131_072;
    private const int ArtifactLimit = 16_384;
    private const int SummaryLimit = 16_384;

    public static async Task<OpenCodeResult> ExecuteTaskAsync(
        OpenCodeTask task,
        Settings settings,
        string executable = "opencode",
        CancellationToken cancellationToken = default)
    {
        var prompt = task.Prompt;
        var taskId = task.TaskId;
        if (prompt is null || prompt.Length is < 1 or > 8000)
        {
            throw Fail("Task prompt must contain 1 to 8000 characters.");
        }
        if (taskId is null || taskId.Length is < 1 or > 100)
        {
            throw Fail("Task ID is missing or invalid.");
        }

        var auth = Path.Combine(settings.OpenCodeDataHome, "opencode", "auth.json");
        if (!File.Exists(auth))
        {
            throw Fail("OpenCode is not signed in. Run the opencode-login Compose service first.");
        }

        var secrets = AuthValues(auth);
        var workspace = Directory.CreateTempSubdirectory("opencode-task-");
        try
        {
            var (exitCode, output, errors) = await RunProcessAsync(
                executable, prompt, workspace.FullName, settings, cancellationToken);

            if (exitCode != 0)
            {
                throw Fail(
                    $"OpenCode exited with code {exitCode}: "
                    + FailureDetail(errors, [.. secrets, .. AuthValues(auth)]));
            }

            var summary = AssistantText(output);
            var artifacts = new Dictionary<string, string>();
            foreach (var name in ArtifactNames)
            {
                artifacts[name] = ReadArtifact(Path.Combine(workspace.FullName, name), name);
            }
            return new OpenCodeResult(taskId, settings.OpenCodeModel, summary, artifacts);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static async Task<(int ExitCode, byte[] Output, byte[] Errors)> RunProcessAsync(
        string executable,
        string prompt,
        string workspace,
        Settings settings,
        CancellationToken cancellationToken)
    {
        // setsid gives the child its own process group, so the whole tree can be killed at once.
        var start = new ProcessStartInfo("setsid")
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // No shell: the prompt is a single argument and never interpreted.
        foreach (var argument in new[]
        {
            executable, "--print-logs", "run", "--format", "json", "--model",
            settings.OpenCodeModel, "--", prompt,
        })
        {
            start.ArgumentList.Add(argument);
        }

        // Explicit environment: no database passwords, webhook secret, or Temporal settings.
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "HOME", "LANG", "SSL_CERT_FILE", "SSL_CERT_DIR" })
        {
            if (Environment.GetEnvironmentVariable(key) is { } value)
            {
                start.Environment[key] = value;
            }
        }
        start.Environment["XDG_DATA_HOME"] = settings.OpenCodeDataHome;
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(workspace, ".cache");
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(workspace, ".config");
        start.Environment["XDG_STATE_HOME"] = Path.Combine(workspace, ".state");
        start.Environment["OPENCODE_CONFIG"] = "/etc/opencode/opencode.json";
        start.Environment["CI"] = "true";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start OpenCode.");
        var stdout = ReadLimitedAsync(process.StandardOutput.BaseStream);
        var stderr = ReadLimitedAsync(process.StandardError.BaseStream);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.OpenCodeTimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Fail("OpenCode task timed out.");
            }
        }
        finally
        {
            // Kill any descendants too, including on Temporal cancellation.
            _ = Kill(-process.Id, 9);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>Drains the pipe without retaining unbounded model or CLI output.</summary>
    private static async Task<byte[]> ReadLimitedAsync(Stream stream)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            var room = StreamLimit - (int)output.Length;
            if (room > 0)
            {
                output.Write(buffer, 0, Math.Min(read, room));
            }
        }
        return output.ToArray();
    }

    internal static string AssistantText(byte[] output)
    {
        var messages = new List<string>();
        foreach (var line in Encoding.UTF8.GetString(output).Split('\n'))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var type = root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;
                if (type == "error")
                {
                    throw Fail("OpenCode reported an error; check the ChatGPT login and selected model.");
                }
                if (type == "text"
                    && root.TryGetProperty("part", out var part)
                    && part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    messages.Add(text.GetString()!);
                }
            }
        }
        var joined = string.Join('\n', messages);
        return joined.Length > SummaryLimit ? joined[..SummaryLimit] : joined;
    }

    internal static List<string> AuthValues(string path)
    {
        var values = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Collect(document.RootElement, values);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
        return values;
    }

    private static void Collect(JsonElement element, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, values);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, values);
                }
                break;
            case JsonValueKind.String when element.GetString() is { Length: > 10 } text:
                values.Add(text);
                break;
        }
    }

    internal static string FailureDetail(byte[] stderr, IEnumerable<string> secrets)
    {
        var text = Encoding.UTF8.GetString(stderr);
        foreach (var secret in secrets)
        {
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }
        text = BearerPattern().Replace(text, "Bearer [REDACTED]");
        var lines = text.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("Error:", StringComparison.Ordinal))
            .ToList();
        var detail = lines.Count > 0 ? lines[^1] : "Check the ChatGPT login and selected model.";
        return detail.Length > 1800 ? detail[..1800] : detail;
    }

    private static string ReadArtifact(string path, string name)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || !info.Exists)
        {
            throw Fail($"OpenCode did not create a regular {name} file.");
        }
        if (info.Length > ArtifactLimit)
        {
            throw Fail($"Artifact {name} is too large.");
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException)
        {
            throw Fail($"Artifact {name} is not UTF-8 text.");
        }
    }

    private static ApplicationFailureException Fail(string message) =>
        new(message, nonRetryable: true);

    [GeneratedRegex("""Bearer\s+[^\s\\"]+""")]
    private static partial Regex BearerPattern();

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);
}
