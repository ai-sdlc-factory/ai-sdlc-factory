using System.Globalization;
using System.Text.Json;

namespace DevFoundry.Controller;

/// <summary>
/// Forgiving readers for what a git host sends back. A part that is missing or has another type reads as empty (or null), never as an
/// error, and none of them throws on an element that is not an object: a host's answers vary between versions and a missing field must not
/// stop a run.
/// </summary>
internal static class HostJson
{
    public static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>A part that is an object, or an empty element (which <see cref="Text"/> reads as empty text).</summary>
    public static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    /// <summary>The login of the <c>user</c> object of a review or comment.</summary>
    public static string Login(JsonElement element) => Text(Child(element, "user"), "login");

    /// <summary>A time field. Hosts send ISO 8601; one without an offset is taken as UTC.</summary>
    public static DateTimeOffset? When(JsonElement element, string name) =>
        DateTimeOffset.TryParse(Text(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>A whole number field, or 0.</summary>
    public static int Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    /// <summary>The text of a file as Forgejo and GitHub both answer a contents request: a <c>file</c> with base64 <c>content</c>. Null for anything else, or a file that is too large.</summary>
    public static string? FileText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || Text(root, "type") != "file" || Text(root, "encoding") != "base64" || Count(root, "size") > GitHostLimits.MaxFileBytes)
        {
            return null;
        }
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Text(root, "content")));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A pull request as Forgejo and GitHub both describe it (the same shape: <c>head</c>, <c>base</c>, <c>user</c>, <c>merged</c>).</summary>
    public static PullRequestDetails PullRequest(JsonElement root, int number)
    {
        static string Repository(JsonElement side) => Text(Child(side, "repo"), "full_name");
        var head = Child(root, "head");
        var @base = Child(root, "base");
        return new PullRequestDetails(
            number, Text(root, "title"), Text(root, "body"), Text(root, "state"),
            root.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True,
            Text(head, "ref"), Text(head, "sha"), Repository(head), Repository(@base), Text(@base, "ref"), Text(Child(root, "user"), "login"));
    }

    /// <summary>One entry of a pull request's file list, with the host's status written in the factory's words (<paramref name="status"/> maps it; none means as it is).</summary>
    public static ChangedFile ChangedFile(JsonElement item, Func<string, string>? status = null) =>
        new(Text(item, "filename"), status is null ? Text(item, "status") : status(Text(item, "status")), Count(item, "additions"), Count(item, "deletions"),
            Text(item, "previous_filename") is { Length: > 0 } previous ? previous : null);

    /// <summary>One comment of a review. The line is the first of <paramref name="lineFields"/> that is a positive number (hosts name it differently, and drop it once the code moved).</summary>
    public static ReviewComment ReviewComment(JsonElement comment, params string[] lineFields)
    {
        int? line = null;
        foreach (var name in lineFields)
        {
            if (comment.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var lineNumber) && lineNumber > 0)
            {
                line = lineNumber;
                break;
            }
        }
        return new ReviewComment(
            comment.TryGetProperty("id", out var commentId) && commentId.ValueKind == JsonValueKind.Number ? commentId.GetInt64() : 0,
            Login(comment), Text(comment, "path") is { Length: > 0 } filePath ? filePath : null, line, Text(comment, "body"),
            Text(comment, "diff_hunk") is { Length: > 0 } hunk ? hunk : null);
    }
}
