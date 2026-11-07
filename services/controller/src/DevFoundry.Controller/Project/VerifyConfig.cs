using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFoundry.Controller;

/// <summary>
/// How the factory can try a finished change for real (the <c>verify</c> key of <c>.factory/project.json</c>), used by the <c>/qa</c> run: which
/// command starts the application, how to tell it is up, where the end-to-end tests live, and the suites (<c>api</c> and/or <c>ui</c>) that run them.
/// </summary>
/// <param name="Start">The command that starts the application, or null when the tests start it themselves.</param>
/// <param name="StartDirectory">The folder the application is started in, when it is not the repository root.</param>
/// <param name="Ready">A local URL that answers once the application is up (any answer below 500 counts); required with <paramref name="Start"/>.</param>
/// <param name="StartupSeconds">How long to wait for <paramref name="Ready"/>.</param>
/// <param name="Tests">The folder where the end-to-end tests live. Only the <c>/qa</c> run may change it: it is a protected path for every other run.</param>
/// <param name="Suites">The suites, each a command (a <see cref="Gate"/>) that runs the tests of one kind.</param>
/// <param name="Image">The sandbox image the suites run in (for example one with a browser), or null for the project's own image.</param>
public sealed record VerifyConfig(
    IReadOnlyList<string>? Start, string? StartDirectory, string? Ready, int StartupSeconds, string Tests, IReadOnlyList<Gate> Suites, string? Image = null)
{
    public const string Api = "api";
    public const string Ui = "ui";

    /// <summary>The folder where the suites may leave evidence (page snapshots and the like). It is read for the report and removed before anything is pushed.</summary>
    public string EvidenceDirectory => $"{Tests}/evidence";

    /// <summary>The pattern that makes the tests folder a protected path for the developer.</summary>
    public string ProtectedPattern => $"{Tests}/**";

    public bool Has(string suite) => Suites.Any(s => s.Name == suite);
}

public static partial class ProjectConfigParser
{
    public const int DefaultStartupSeconds = 60;

    [GeneratedRegex(@"\Ahttp://(?:127\.0\.0\.1|localhost):[0-9]{2,5}(?:/[A-Za-z0-9._~/%?=&:@+-]{0,150})?\z")]
    private static partial Regex LocalUrl();

    private static bool TryReadVerify(JsonElement root, out VerifyConfig? verify, out string error)
    {
        verify = null;
        error = "";
        if (!root.TryGetProperty("verify", out var value))
        {
            return true;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = "\"verify\" must be an object with \"tests\" and \"suites\", and usually \"start\" and \"ready\"";
            return false;
        }

        IReadOnlyList<string>? start = null;
        if (value.TryGetProperty("start", out var startValue))
        {
            if (!TryReadCommand(startValue, "verify.start", out var startCommand, out error))
            {
                return false;
            }
            start = startCommand;
        }

        string? startDirectory = null;
        if (value.TryGetProperty("start_cwd", out var cwdValue))
        {
            startDirectory = cwdValue.ValueKind == JsonValueKind.String ? cwdValue.GetString()?.TrimEnd('/') : null;
            if (startDirectory is null || !IsRelativeFolder(startDirectory))
            {
                error = "\"verify.start_cwd\" must be a folder inside the repository, such as \"src/Api\" (letters, digits and . _ - @ + /, no leading / and no ..)";
                return false;
            }
        }

        string? ready = null;
        if (value.TryGetProperty("ready", out var readyValue))
        {
            ready = readyValue.ValueKind == JsonValueKind.String ? readyValue.GetString() : null;
            if (ready is null || !LocalUrl().IsMatch(ready))
            {
                error = "\"verify.ready\" must be a local address such as \"http://127.0.0.1:5080/health\": the application runs in the same sandbox as the tests, so only localhost and 127.0.0.1 are allowed";
                return false;
            }
        }
        if (start is not null && ready is null)
        {
            error = "\"verify.start\" needs \"verify.ready\": the address that answers once the application is up";
            return false;
        }
        if (start is null && (ready is not null || startDirectory is not null))
        {
            error = "\"verify.ready\" and \"verify.start_cwd\" only make sense with \"verify.start\"";
            return false;
        }

        var startup = DefaultStartupSeconds;
        if (value.TryGetProperty("startup_timeout_seconds", out var startupValue)
            && (startupValue.ValueKind != JsonValueKind.Number || !startupValue.TryGetInt32(out startup) || startup is < 5 or > 600))
        {
            error = "\"verify.startup_timeout_seconds\" must be between 5 and 600";
            return false;
        }

        var tests = value.TryGetProperty("tests", out var testsValue) && testsValue.ValueKind == JsonValueKind.String ? testsValue.GetString()?.TrimEnd('/') : null;
        if (tests is null || !IsRelativeFolder(tests) || tests.Split('/').Any(part => part.StartsWith('.')))
        {
            error = "\"verify.tests\" must be the folder for the end-to-end tests, such as \"e2e\" (letters, digits and _ - @ + /, no leading / or . and no ..); only /qa runs may change it";
            return false;
        }

        string? image = null;
        if (value.TryGetProperty("image", out var imageValue))
        {
            image = imageValue.ValueKind == JsonValueKind.String ? imageValue.GetString() : null;
            if (image is null || !ImageAlias().IsMatch(image))
            {
                error = "\"verify.image\" must be the name of a sandbox image, such as \"browser\"";
                return false;
            }
        }

        if (!value.TryGetProperty("suites", out var suitesValue) || suitesValue.ValueKind != JsonValueKind.Array || suitesValue.GetArrayLength() is 0 or > 2)
        {
            error = "\"verify.suites\" must be an array of 1 or 2 suites, named \"api\" and/or \"ui\"";
            return false;
        }
        var suites = new List<Gate>();
        foreach (var item in suitesValue.EnumerateArray())
        {
            if (!TryReadGate(item, suites, out var suite, out var gateError))
            {
                error = "verify suite: " + gateError;
                return false;
            }
            if (suite.Name is not (VerifyConfig.Api or VerifyConfig.Ui))
            {
                error = $"verify suite \"{suite.Name}\": the name must be \"api\" or \"ui\"";
                return false;
            }
            suites.Add(suite);
        }

        verify = new VerifyConfig(start, startDirectory, ready, startup, tests, suites, image);
        return true;
    }

    private static bool TryReadCommand(JsonElement value, string what, [NotNullWhen(true)] out IReadOnlyList<string>? command, out string error)
    {
        command = null;
        error = "";
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > MaxArguments)
        {
            error = $"\"{what}\" must be an array of 1 to {MaxArguments} strings, such as [\"npm\", \"start\"]";
            return false;
        }
        var list = new List<string>();
        foreach (var argument in value.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String || argument.GetString() is not { Length: > 0 } text || text.Length > MaxArgumentLength || text.Contains('\0'))
            {
                error = $"every argument of \"{what}\" must be a non-empty string of at most {MaxArgumentLength} characters";
                return false;
            }
            list.Add(text);
        }
        command = list;
        return true;
    }

    private static bool IsRelativeFolder(string folder) =>
        GateDirectory().IsMatch(folder) && !folder.Contains("..") && !folder.StartsWith('/');
}
