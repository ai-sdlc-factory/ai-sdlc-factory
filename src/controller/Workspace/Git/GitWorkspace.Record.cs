using Temporalio.Exceptions;

namespace AiSdlcFactory.Controller;

public sealed partial class GitWorkspace
{
    // Recorded steps wait here, outside every directory a sandbox mounts, until a publish or a rescue commits them.
    private string RecordPath(string root, string name) => $"{root}/.records/{name}";

    /// <summary>Stores one step of the run on the host. It never changes the repository by itself.</summary>
    public Task RecordAsync(RecordEntry recorded)
    {
        var entry = recorded.Kind == "agent" ? recorded with { Model = AgentCatalog.ModelFor(settings.AgentsDirectory, recorded.Slug, settings.Model, settings.RoleModels) } : recorded;
        var root = RequireRoot();
        RequireName(entry.Name);
        if (!RunRecord.IsValidSlug(entry.Slug) || entry.Sequence is < 1 or > 999)
        {
            throw new ApplicationFailureException("Record entry has an invalid name.", nonRetryable: true);
        }
        var directory = RecordPath(root, entry.Name);
        Directory.CreateDirectory(directory);
        File.WriteAllText($"{directory}/{RunRecord.FileName(entry)}", RunRecord.Render(entry, KnownSecrets()));
        // The index and summary are built from these small files when the record is committed.
        File.WriteAllText($"{directory}/{entry.Sequence:000}.json", System.Text.Json.JsonSerializer.Serialize(entry with { Input = "", Output = "" }));
        return Task.CompletedTask;
    }

    private IEnumerable<string> KnownSecrets() => settings.HostTokens;

    /// <summary>Copies the stored steps, an index and a summary into the pristine clone, ready to be committed.</summary>
    private void AddRunRecord(string root, string name, string basePath, RunRecordSpec? spec)
    {
        if (spec is null)
        {
            return;
        }
        if (!RunRecord.IsValidDirectory(spec.Directory) || HasSymlinkOnPath(basePath, spec.Directory))
        {
            throw new ApplicationFailureException("Refusing to publish: the run record location is not allowed.", nonRetryable: true);
        }
        var source = RecordPath(root, name);
        if (!Directory.Exists(source))
        {
            return; // nothing was recorded
        }

        var entries = Directory.GetFiles(source, "*.json").Order()
            .Select(file => System.Text.Json.JsonSerializer.Deserialize<RecordEntry>(File.ReadAllText(file))!)
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }
        var target = $"{basePath}/{spec.Directory}";
        Directory.CreateDirectory(target);
        foreach (var entry in entries)
        {
            File.Copy($"{source}/{RunRecord.FileName(entry)}", $"{target}/{RunRecord.FileName(entry)}", overwrite: true);
        }
        File.WriteAllText($"{target}/README.md", RunRecord.Summary(spec, entries));
        File.WriteAllText($"{target}/index.json", RunRecord.Index(entries));
        if (RunRecord.HasUsage(entries))
        {
            File.WriteAllText($"{target}/usage.json", RunRecord.UsageJson(entries));
        }
    }
}
