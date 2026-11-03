using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DevFoundry.Controller;

/// <summary>
/// Finds the JSON answer in model text. Models wrap JSON in prose or code fences and may quote an example first, so the
/// last object that carries a <c>verdict</c> field wins; the shape itself is checked by the caller's validator.
/// </summary>
public static class AnswerReader
{
    public delegate bool Validator<T>(JsonElement root, [NotNullWhen(true)] out T? value, out string error);

    public static bool TryRead<T>(string? text, Validator<T> validate, [NotNullWhen(true)] out T? value, out string error)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "the reply was empty";
            return false;
        }

        string? lastError = null;
        foreach (var candidate in JsonObjects.TopLevel(text).Reverse())
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(candidate);
            }
            catch (JsonException)
            {
                continue;
            }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("verdict", out _))
                {
                    continue;
                }
                if (validate(document.RootElement, out value, out var problem))
                {
                    error = "";
                    return true;
                }
                lastError = problem;
            }
        }

        error = lastError ?? "no JSON object with a \"verdict\" field was found";
        return false;
    }
}
