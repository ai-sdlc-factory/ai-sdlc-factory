namespace DevFoundry.Controller;

/// <summary>Shared handling of untrusted text that goes into a prompt.</summary>
public static class PromptText
{
    /// <summary>
    /// Makes text safe to place between prompt markers: removes NUL characters first (so removing them cannot rebuild a marker), then breaks up
    /// the <c>&lt;&lt;&lt;</c> and <c>&gt;&gt;&gt;</c> sequences that open and close a block, so the text cannot close its own block.
    /// </summary>
    public static string Sanitize(string value) =>
        value.Replace("\0", "").Replace("<<<", "< < <").Replace(">>>", "> > >");

    /// <summary>Shortens multi-line text to at most <paramref name="max"/> characters and makes it safe (see <see cref="Sanitize"/>).</summary>
    public static string Clip(string? value, int max)
    {
        var clean = Sanitize(value ?? "").Trim();
        return clean.Length > max ? clean[..max] + " [cut]" : clean;
    }

    /// <summary>The same for a value that belongs on one line: line breaks become spaces, so it cannot start a new line of the prompt.</summary>
    public static string ClipLine(string? value, int max) =>
        Clip((value ?? "").Replace("\r", " ").Replace("\n", " "), max);
}
