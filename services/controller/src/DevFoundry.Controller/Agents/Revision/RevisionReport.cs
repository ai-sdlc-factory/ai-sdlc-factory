using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using static DevFoundry.Controller.JsonFields;

namespace DevFoundry.Controller;

/// <summary>One thing a person asked for, numbered so the agent can answer each one: a line comment, a review's overall text, or the text after <c>/fix</c>.</summary>
public sealed record RevisionComment(int Index, string Author, string? Path, int? Line, string Body)
{
    public string Where => Path is null ? "general" : Line is null ? Path : $"{Path}:{Line}";
}

/// <summary>What the agent did about one comment.</summary>
public sealed record RevisionResponse(int Index, string Status, string Note)
{
    public const string Fixed = "fixed";
    public const string Answered = "answered";
    public const string Declined = "declined";
    public const string NeedsClarification = "needs_clarification";

    public static readonly string[] Statuses = [Fixed, Answered, Declined, NeedsClarification];
}

public sealed record RevisionReport(string Verdict, string Summary, IReadOnlyList<RevisionResponse> Responses, IReadOnlyList<string> Notes)
{
    public const string Done = "done";
    public const string Blocked = "blocked";
}

public static class RevisionParser
{
    public const int MaxNote = 300;

    /// <summary>
    /// Validates the agent's answer against the comments it was given: every comment must be answered exactly once. A reply that
    /// leaves one out, answers one twice or invents an index is refused with the exact problem.
    /// </summary>
    public static bool TryParse(string? text, int commentCount, [NotNullWhen(true)] out RevisionReport? report, out string error) =>
        AnswerReader.TryRead<RevisionReport>(text, (JsonElement root, [NotNullWhen(true)] out RevisionReport? value, out string problem) => Validate(root, commentCount, out value, out problem), out report, out error);

    private static bool Validate(JsonElement root, int commentCount, [NotNullWhen(true)] out RevisionReport? report, out string error)
    {
        report = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (RevisionReport.Done or RevisionReport.Blocked))
        {
            error = "\"verdict\" must be one of done, blocked";
            return false;
        }
        var summary = Text(root, "summary")?.Trim();
        if (string.IsNullOrEmpty(summary) || summary.Length > 2000)
        {
            error = "\"summary\" must be a non-empty string of at most 2000 characters";
            return false;
        }
        if (!TextList(root, "notes", out var notes, out error, maxItems: 10, maxLength: 500))
        {
            return false;
        }

        var responses = new List<RevisionResponse>();
        if (verdict == RevisionReport.Done)
        {
            if (!root.TryGetProperty("responses", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                error = $"\"responses\" must be an array with one entry for each of the {commentCount} comments";
                return false;
            }
            var seen = new HashSet<int>();
            foreach (var item in array.EnumerateArray())
            {
                var index = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) ? n : -1;
                var status = item.ValueKind == JsonValueKind.Object ? Text(item, "status") : null;
                var note = item.ValueKind == JsonValueKind.Object ? Text(item, "note")?.Trim() ?? "" : "";
                if (index < 1 || index > commentCount)
                {
                    error = $"every response needs an \"index\" from 1 to {commentCount}";
                    return false;
                }
                if (!seen.Add(index))
                {
                    error = $"comment {index} is answered more than once";
                    return false;
                }
                if (status is null || !RevisionResponse.Statuses.Contains(status))
                {
                    error = $"response {index}: \"status\" must be one of {string.Join(", ", RevisionResponse.Statuses)}";
                    return false;
                }
                if (note.Length > MaxNote || (status != RevisionResponse.Fixed && note.Length == 0))
                {
                    error = $"response {index}: \"note\" is required unless the status is fixed, and at most {MaxNote} characters";
                    return false;
                }
                responses.Add(new RevisionResponse(index, status, note));
            }
            var missing = Enumerable.Range(1, commentCount).Where(index => !seen.Contains(index)).ToList();
            if (missing.Count > 0)
            {
                error = $"comment {string.Join(", ", missing)} {(missing.Count == 1 ? "has" : "have")} no response; answer every comment";
                return false;
            }
        }

        report = new RevisionReport(verdict, summary, responses.OrderBy(r => r.Index).ToList(), notes);
        return true;
    }
}

public static class RevisionComments
{
    /// <summary>
    /// The comments to act on, numbered from 1: what was written after <c>/fix</c> (if anything), the review's overall text, then its comments
    /// on lines. Over the limit, the rest is left out and the agent is not told about it, because it could not answer what it never saw.
    /// </summary>
    public static IReadOnlyList<RevisionComment> Collect(RevisionRequest request, ReviewDetails? review)
    {
        var list = new List<RevisionComment>();
        void Add(string author, string? path, int? line, string? body)
        {
            if (!string.IsNullOrWhiteSpace(body) && list.Count < RevisionRules.MaxComments)
            {
                list.Add(new RevisionComment(list.Count + 1, author, path, line, body.Trim()));
            }
        }

        Add(request.Author, null, null, request.Instruction);
        if (review is not null)
        {
            // A review's overall text that is the same as the trigger's (the review event carries it) is not listed twice.
            if (!string.Equals(review.Body?.Trim(), request.Instruction?.Trim(), StringComparison.Ordinal))
            {
                Add(review.Reviewer, null, null, review.Body);
            }
            foreach (var comment in review.Comments)
            {
                Add(comment.Author, string.IsNullOrWhiteSpace(comment.Path) ? null : comment.Path, comment.Line, comment.Body);
            }
        }
        return list;
    }
}
