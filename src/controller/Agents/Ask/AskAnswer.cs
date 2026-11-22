using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using static AiSdlcFactory.Controller.JsonFields;

namespace AiSdlcFactory.Controller;

/// <summary>What the <c>asker</c> concluded: an answer read from the repository, or that the repository does not say. <see cref="Files"/> are the files it says it read (checked before they are shown).</summary>
public sealed record AskAnswer(string Verdict, string Answer, IReadOnlyList<string> Files)
{
    public const string Answered = "answered";
    public const string CannotAnswer = "cannot_answer";
}

public static partial class AskParser
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\A[A-Za-z0-9._][A-Za-z0-9._/-]{0,199}\z")]
    private static partial System.Text.RegularExpressions.Regex PlainFile();

    /// <summary>A relative file path in plain characters. Unlike <see cref="ImprovementParser.IsPlainPath"/> it may start with a dot (<c>.factory/project.json</c>), because answers cite such files.</summary>
    private static bool IsUsablePath(string path) => PlainFile().IsMatch(path) && !path.Contains("..") && !path.Contains("//");

    public const int MaxAnswer = 6000;
    public const int MaxFiles = 10;

    public static bool TryParse(string? text, [NotNullWhen(true)] out AskAnswer? result, out string error) =>
        AnswerReader.TryRead<AskAnswer>(text, Validate, out result, out error);

    private static bool Validate(JsonElement root, [NotNullWhen(true)] out AskAnswer? result, out string error)
    {
        result = null;
        var verdict = Text(root, "verdict");
        if (verdict is not (AskAnswer.Answered or AskAnswer.CannotAnswer))
        {
            error = "\"verdict\" must be one of answered, cannot_answer";
            return false;
        }
        var answer = Text(root, "answer")?.Trim();
        if (string.IsNullOrEmpty(answer) || answer.Length > MaxAnswer)
        {
            error = $"\"answer\" must be a non-empty string of at most {MaxAnswer} characters; be shorter and point to files for the detail";
            return false;
        }
        // The files are only a list of what was read, and each one is checked to exist before it is shown, so an entry that is not a usable path is dropped
        // instead of throwing away a good answer (and the tokens it cost).
        var files = root.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!.Trim()).Where(IsUsablePath)
            : [];
        result = new AskAnswer(verdict, answer, files.Distinct().Take(MaxFiles).ToList());
        error = "";
        return true;
    }
}
