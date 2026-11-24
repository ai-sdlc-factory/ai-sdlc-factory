namespace AiSdlcFactory.Controller;

/// <summary>
/// <c>validate-project &lt;file|-&gt;</c>: reads a <c>.factory/project.json</c> with the same reader the runs use and says whether it is usable, so a mistake (a limit out of range, an unknown
/// key) is found when the file is written and not when the first ticket arrives. <c>-</c> reads the file from standard input. Exit code 0: usable; 1: not usable; 2: it could not be read at all.
/// It checks the file on its own: what depends on the operator's settings (a limit's own maximum, which images exist) is only known to the running factory.
/// </summary>
public static class ProjectCheckCommand
{
    public const string Name = "validate-project";

    public static int Run(string[] arguments, TextReader input, TextWriter output)
    {
        if (arguments.Length != 1)
        {
            output.WriteLine($"Usage: {Name} <path to project.json | - for standard input>");
            return 2;
        }
        string text;
        try
        {
            text = arguments[0] == "-" ? input.ReadToEnd() : File.ReadAllText(arguments[0]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Cannot read {arguments[0]}: {error.Message}");
            return 2;
        }
        if (ProjectConfigParser.TryParse(text, out _, out var problem))
        {
            output.WriteLine(".factory/project.json is usable.");
            return 0;
        }
        output.WriteLine($".factory/project.json cannot be used: {problem}");
        return 1;
    }
}
