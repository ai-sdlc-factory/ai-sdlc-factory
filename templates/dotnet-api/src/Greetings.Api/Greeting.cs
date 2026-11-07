namespace Greetings.Api;

/// <summary>The one piece of business logic in this demo: build a greeting for a name, or refuse a name that is not usable.</summary>
public static class Greeting
{
    public const int MaxNameLength = 50;

    /// <summary>Null when the name is empty, too long, or contains anything but letters, digits, spaces, dots and dashes.</summary>
    public static string? For(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength || !trimmed.All(IsAllowed))
        {
            return null;
        }
        return $"Hello, {trimmed}!";
    }

    private static bool IsAllowed(char c) => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-';
}
