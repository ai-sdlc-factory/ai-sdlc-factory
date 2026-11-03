namespace DevFoundry.Controller;

/// <summary>Shared handling of untrusted text that goes into a prompt.</summary>
public static class PromptText
{
    /// <summary>Shortens text and removes the marker sequences so it cannot close its own block.</summary>
    public static string Clip(string? value, int max)
    {
        var clean = (value ?? "").Replace("<<<", "< < <").Replace(">>>", "> > >").Replace("\0", "").Trim();
        return clean.Length > max ? clean[..max] + " [cut]" : clean;
    }
}
