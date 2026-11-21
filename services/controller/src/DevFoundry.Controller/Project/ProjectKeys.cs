using System.Text.Json;

namespace DevFoundry.Controller;

/// <summary>
/// The keys <c>.factory/project.json</c> understands, scope by scope, so that a key the factory does not know (almost always a typo, such as
/// <c>"protected_path"</c> or <c>"revewers"</c>) is reported instead of being silently ignored. An unknown key is a warning, not an error: the run goes ahead
/// with the file as it is read. Every key a reader in this folder asks for must be listed here; <c>tools/check-docs.py</c> checks that.
/// </summary>
public static class ProjectKeys
{
    private static readonly string[] Root = ["image", "gates", "reviewers", "ui", "record", "mode", "rules", "limits", "protected_paths", "verify", "commands"];
    private static readonly string[] Gate = ["name", "command", "timeout_seconds", "cwd"];
    private static readonly string[] Ui = ["paths", "medium", "large"];
    private static readonly string[] Threshold = ["files", "lines"];
    private static readonly string[] Rule = ["id", "files", "forbid", "message", "severity"];
    private static readonly string[] Verify = ["start", "start_cwd", "ready", "startup_timeout_seconds", "tests", "image", "suites"];
    private const int MaxWarnings = 10;

    /// <summary>One sentence for each key in the file that the factory does not know, with the closest known key when there is one. Empty for a file with no such key.</summary>
    public static IReadOnlyList<string> Warnings(JsonElement root)
    {
        var warnings = new List<string>();
        Check(root, Root, "", warnings);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("commands", out var commands))
        {
            // Who may steer the factory is read by the permission check, not by the run; say here when what the project wrote cannot be used.
            try
            {
                ProjectCommandRule.Read(commands, "`commands`");
            }
            catch (InvalidOperationException error)
            {
                warnings.Add(error.Message);
            }
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var gate in Items(root, "gates"))
            {
                Check(gate, Gate, "gates[]", warnings);
            }
            foreach (var rule in Items(root, "rules"))
            {
                Check(rule, Rule, "rules[]", warnings);
            }
            if (root.TryGetProperty("ui", out var ui))
            {
                Check(ui, Ui, "ui", warnings);
                if (ui.ValueKind == JsonValueKind.Object)
                {
                    foreach (var size in new[] { "medium", "large" })
                    {
                        if (ui.TryGetProperty(size, out var threshold))
                        {
                            Check(threshold, Threshold, $"ui.{size}", warnings);
                        }
                    }
                }
            }
            if (root.TryGetProperty("verify", out var verify))
            {
                Check(verify, Verify, "verify", warnings);
                foreach (var suite in Items(verify, "suites"))
                {
                    Check(suite, Gate, "verify.suites[]", warnings);
                }
            }
        }
        return warnings.Count > MaxWarnings ? [.. warnings.Take(MaxWarnings)] : warnings;
    }

    private static IEnumerable<JsonElement> Items(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static void Check(JsonElement element, string[] known, string scope, List<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (known.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }
            var place = scope.Length == 0 ? "" : $" in `{scope}`";
            var name = Printable(property.Name);
            var guess = Closest(property.Name, known);
            warnings.Add(guess is null
                ? $"`{name}`{place} is not a setting the factory knows, so it is ignored"
                : $"`{name}`{place} is not a setting the factory knows, so it is ignored. Did you mean `{guess}`?");
        }
    }

    /// <summary>The known key a typo is most likely meant to be: one it is the start of (`timeout` for `timeout_seconds`), else the nearest by edit distance, if that is at most 2 (and less than the typo's length).</summary>
    private static string? Closest(string name, string[] known)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Length >= 4 && known.FirstOrDefault(key => key.StartsWith(lower, StringComparison.Ordinal)) is { } longer)
        {
            return longer;
        }
        var best = known.Select(key => (Key: key, Distance: Distance(lower, key))).OrderBy(pair => pair.Distance).First();
        return best.Distance <= 2 && best.Distance < lower.Length ? best.Key : null;
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    /// <summary>A key is the repository's text: it is shortened and stripped of anything that would break a comment.</summary>
    private static string Printable(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c) && c != '`').ToArray());
        return clean.Length > 40 ? clean[..40] + "..." : clean;
    }
}
