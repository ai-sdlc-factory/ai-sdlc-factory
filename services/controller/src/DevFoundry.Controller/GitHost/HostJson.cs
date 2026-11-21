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

    /// <summary>A time field. Forgejo sends ISO 8601; one without an offset is taken as UTC.</summary>
    public static DateTimeOffset? When(JsonElement element, string name) =>
        DateTimeOffset.TryParse(Text(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}
