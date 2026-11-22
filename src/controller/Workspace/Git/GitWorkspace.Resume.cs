using System.Text.Json;
using Temporalio.Exceptions;

namespace AiSdlcFactory.Controller;

public sealed record SaveResumeRequest(string Name, ResumeState State);

public sealed partial class GitWorkspace
{
    private const long MaxResumeBytes = 256 * 1024;

    private static string ResumePath(string root, string name) => $"{root}/.resume/{name}.json";

    /// <summary>
    /// Keeps what a run has finished (see <see cref="ResumeState"/>) next to the checkouts, one small file per ticket, written as a whole or not at all.
    /// Kept apart from the checkout so that removing the checkout at the end of a run does not remove it; the janitor removes it when it is old.
    /// </summary>
    public Task SaveResumeAsync(SaveResumeRequest request)
    {
        var root = RequireRoot();
        RequireName(request.Name);
        var json = JsonSerializer.Serialize(request.State);
        if (json.Length > MaxResumeBytes)
        {
            return Task.CompletedTask; // never worth failing a run for: the next /retry just starts from the beginning
        }
        Directory.CreateDirectory($"{root}/.resume");
        var path = ResumePath(root, request.Name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
        return Task.CompletedTask;
    }

    /// <summary>The saved record for a ticket, or null when there is none or it cannot be read (an unreadable file is the same as none).</summary>
    public ResumeState? LoadResume(string name)
    {
        var root = RequireRoot();
        RequireName(name);
        var file = new FileInfo(ResumePath(root, name));
        if (!file.Exists || file.LinkTarget is not null || file.Length > MaxResumeBytes)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(file.FullName));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void ClearResume(string name)
    {
        var root = RequireRoot();
        RequireName(name);
        var path = ResumePath(root, name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
