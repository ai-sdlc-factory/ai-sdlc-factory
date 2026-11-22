using System.Text;
using System.Text.RegularExpressions;

namespace AiSdlcFactory.Controller;

/// <summary>
/// File path patterns. <c>*</c> matches within one folder level, <c>**</c> across levels (<c>**/</c> also
/// matches no folder at all), <c>?</c> one character. A pattern without a slash is matched against the file
/// name wherever the file is, so <c>*.css</c> means every stylesheet.
/// </summary>
public static partial class Globs
{
    [GeneratedRegex(@"\A[A-Za-z0-9._/*?@+-]{1,100}\z")]
    private static partial Regex Allowed();

    public static bool IsValidPattern(string pattern) =>
        Allowed().IsMatch(pattern) && !pattern.StartsWith('/') && !pattern.Contains("..");

    public static bool IsMatch(string path, string pattern)
    {
        var target = pattern.Contains('/') ? path : path[(path.LastIndexOf('/') + 1)..];
        return ToRegex(pattern).IsMatch(target);
    }

    public static bool IsMatchAny(string path, IEnumerable<string> patterns) => patterns.Any(pattern => IsMatch(path, pattern));

    private static Regex ToRegex(string pattern)
    {
        var text = new StringBuilder("\\A");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                {
                    text.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    text.Append(".*");
                    i += 1;
                }
            }
            else if (c == '*')
            {
                text.Append("[^/]*");
            }
            else if (c == '?')
            {
                text.Append("[^/]");
            }
            else
            {
                text.Append(Regex.Escape(c.ToString()));
            }
        }
        text.Append("\\z");
        return new Regex(text.ToString(), RegexOptions.CultureInvariant);
    }
}
