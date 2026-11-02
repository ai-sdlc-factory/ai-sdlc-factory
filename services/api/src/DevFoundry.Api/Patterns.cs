using System.Text.RegularExpressions;

namespace DevFoundry.Api;

internal static partial class Patterns
{
    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z")]
    public static partial Regex Signature();

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,63}\z")]
    public static partial Regex EventName();
}
