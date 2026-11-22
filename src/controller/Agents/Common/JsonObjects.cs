namespace AiSdlcFactory.Controller;

/// <summary>Finds JSON objects inside text that models wrap in prose or code fences.</summary>
public static class JsonObjects
{
    /// <summary>Balanced {...} spans at nesting depth zero, ignoring braces inside JSON strings.</summary>
    public static IEnumerable<string> TopLevel(string text)
    {
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"' when depth > 0:
                    inString = true;
                    break;
                case '{':
                    if (depth++ == 0) start = i;
                    break;
                case '}' when depth > 0:
                    if (--depth == 0) yield return text[start..(i + 1)];
                    break;
            }
        }
    }
}
