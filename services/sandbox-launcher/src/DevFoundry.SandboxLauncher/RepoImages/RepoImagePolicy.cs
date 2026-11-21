using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Temporalio.Exceptions;

namespace DevFoundry.SandboxLauncher;

/// <summary>A Dockerfile and context that passed the policy.</summary>
public sealed record ValidatedBuild(string Dockerfile, string BaseImage, IReadOnlyList<(string Path, byte[] Content)> Files);

/// <summary>
/// What a repository's Dockerfile may contain. The file is untrusted text that will run on this host's Docker, so the policy is narrow: it must
/// start from one of the factory's own images (which guarantees git, the agent and the entrypoint are there), use a short list of instructions
/// that cannot change how the container starts, and fetch nothing at build time except through RUN.
/// </summary>
public static partial class RepoImagePolicy
{
    public const int MaxDockerfileBytes = 64 * 1024;
    public const int MaxInstructions = 200;
    public const int MaxFiles = 50;
    public const int MaxContextBytes = 1024 * 1024;
    private static readonly string[] AllowedInstructions = ["RUN", "COPY", "ADD", "ENV", "ARG", "LABEL"];

    [GeneratedRegex(@"\A[A-Za-z0-9._@+/-]{1,200}\z")]
    private static partial Regex FilePath();

    [GeneratedRegex(@"^\s*#\s*(syntax|escape|check)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ParserDirective();

    /// <summary>
    /// Checks a repository's Dockerfile and context against the rules in the type's description, in the order a reader would: the text,
    /// the first instruction, the rest of the instructions, then the context files. The first problem found stops it, with a message the
    /// repository's owner can act on.
    /// </summary>
    public static ValidatedBuild Validate(SandboxBuildRequest request, IReadOnlyCollection<string> allowedBaseImages)
    {
        var text = request.Dockerfile ?? "";
        if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > MaxDockerfileBytes || text.Contains('\0'))
        {
            throw Reject($"the Dockerfile must be 1 byte to {MaxDockerfileBytes / 1024} KB of text.");
        }

        var instructions = ReadInstructions(text);
        var baseImage = CheckBaseImage(instructions[0], allowedBaseImages);
        foreach (var instruction in instructions.Skip(1))
        {
            CheckInstruction(instruction);
        }
        return new ValidatedBuild(text, baseImage, ReadContext(request.Files));
    }

    /// <summary>
    /// The instructions of a Dockerfile, one string each: comments and blank lines dropped, a line ending in a backslash joined to the next.
    /// A parser directive (<c># syntax=</c>) is refused, because it can pull in a different builder.
    /// </summary>
    private static List<string> ReadInstructions(string text)
    {
        var instructions = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (ParserDirective().IsMatch(raw))
            {
                throw Reject("parser directives such as '# syntax=' are not allowed.");
            }
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue; // blank lines and comments also inside a continued instruction
            }
            if (line.EndsWith('\\'))
            {
                current.Append(line.AsSpan(0, line.Length - 1)).Append(' ');
                continue;
            }
            current.Append(line);
            instructions.Add(current.ToString().Trim());
            current.Clear();
        }
        if (current.Length > 0)
        {
            instructions.Add(current.ToString().Trim());
        }
        if (instructions.Count == 0 || instructions.Count > MaxInstructions)
        {
            throw Reject($"the Dockerfile must have 1 to {MaxInstructions} instructions.");
        }
        return instructions;
    }

    /// <summary>The first instruction must be exactly <c>FROM &lt;image&gt;</c> with one of the factory's images; returns that image.</summary>
    private static string CheckBaseImage(string firstInstruction, IReadOnlyCollection<string> allowedBaseImages)
    {
        var first = firstInstruction.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!first[0].Equals("FROM", StringComparison.OrdinalIgnoreCase) || first.Length != 2 || !allowedBaseImages.Contains(first[1]))
        {
            throw Reject($"the first instruction must be exactly 'FROM <image>' with one of the factory's images: {string.Join(", ", allowedBaseImages.Order())}.");
        }
        return first[1];
    }

    /// <summary>Every instruction after FROM must be on the short list, and may not reach outside the context or ask for extra privileges.</summary>
    private static void CheckInstruction(string instruction)
    {
        var parts = instruction.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keyword = parts[0].ToUpperInvariant();
        if (!AllowedInstructions.Contains(keyword))
        {
            throw Reject($"the instruction {keyword} is not allowed (allowed: {string.Join(", ", AllowedInstructions)}; one FROM, first).");
        }
        var flags = parts.Skip(1).TakeWhile(part => part.StartsWith("--", StringComparison.Ordinal)).Select(part => part.ToLowerInvariant()).ToList();
        if (keyword is "COPY" or "ADD")
        {
            if (flags.Any(flag => !flag.StartsWith("--chown=", StringComparison.Ordinal) && !flag.StartsWith("--chmod=", StringComparison.Ordinal)))
            {
                throw Reject($"{keyword} may only use the flags --chown and --chmod (not --from).");
            }
            if (keyword == "ADD" && parts.Skip(1).Any(part => part.Contains("://", StringComparison.Ordinal) || part.StartsWith("git@", StringComparison.Ordinal)))
            {
                throw Reject("ADD may not fetch a URL; use RUN with the tool you trust, or COPY a file from .factory/docker/.");
            }
        }
        else if (keyword == "RUN" && flags.Count > 0)
        {
            throw Reject("RUN may not use flags such as --mount, --network or --security.");
        }
    }

    /// <summary>The context files (path to base64 content): plain relative paths, valid base64, within the file and size limits, sorted by path.</summary>
    private static List<(string Path, byte[] Content)> ReadContext(IReadOnlyDictionary<string, string>? encodedFiles)
    {
        var files = new List<(string Path, byte[] Content)>();
        long total = 0;
        foreach (var (path, encoded) in encodedFiles ?? new Dictionary<string, string>())
        {
            if (!FilePath().IsMatch(path) || path.StartsWith('/') || path.EndsWith('/') || path.Split('/').Any(segment => segment is "" or "." or "..")
                || path.Equals("Dockerfile", StringComparison.Ordinal))
            {
                throw Reject($"the context file '{path}' has an unusable path.");
            }
            byte[] content;
            try
            {
                content = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                throw Reject($"the context file '{path}' is not valid base64.");
            }
            total += content.Length;
            files.Add((path, content));
        }
        if (files.Count > MaxFiles || total > MaxContextBytes)
        {
            throw Reject($"the build context may have at most {MaxFiles} files and {MaxContextBytes / 1024} KB.");
        }
        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Twelve hex characters that change when the base image, the Dockerfile or any context file changes.</summary>
    public static string Hash(string baseImageId, ValidatedBuild build)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string part) => sha.AppendData(Encoding.UTF8.GetBytes(part + "\0"));
        Add(baseImageId);
        Add(build.Dockerfile);
        foreach (var (path, content) in build.Files)
        {
            Add(path);
            Add(Convert.ToHexString(SHA256.HashData(content)));
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant();
    }

    // A build that will not fix itself on a retry.
    public static ApplicationFailureException Reject(string message) =>
        new($"Repository image rejected: {message}", errorType: "RepoImagePolicy", nonRetryable: true);
}
