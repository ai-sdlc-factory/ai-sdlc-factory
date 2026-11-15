using System.Security.Cryptography;
using System.Text;

namespace DevFoundry.Controller;

/// <summary>
/// What a run of a feature or bug ticket had finished, kept on the controller between runs so that <c>/retry</c> can carry on from the last good
/// stage instead of asking the same agents (and the same questions) again. Only results that cost model usage and cannot change without the ticket or the
/// repository changing are kept: the analysis (with the answers people gave to its questions), the coverage check and the design. The developer's work is not
/// kept here; a run that stops with work saves it to a <c>-wip</c> branch. A saved result is used only if the ticket and the default branch are as they were.
/// </summary>
public sealed record ResumeState(
    int Version, string Fingerprint, string HeadCommit,
    AnalystVerdict? Analysis = null, CoverageReport? Coverage = null, ArchitectDesign? Design = null, BugAnalysis? BugAnalysis = null)
{
    public const int CurrentVersion = 1;

    /// <summary>An empty record for this ticket as the run found it.</summary>
    public static ResumeState Start(WorkRequest request, WorkspaceInfo workspace) =>
        new(CurrentVersion, FingerprintOf(request), workspace.HeadCommit);

    /// <summary>Stands for what the stages read from the ticket: its kind, title, text and labels (labels change the mode of a run).</summary>
    public static string FingerprintOf(WorkRequest request)
    {
        var labels = (request.Labels ?? []).Select(label => label.ToLowerInvariant()).Order(StringComparer.Ordinal);
        var text = string.Join('\n', request.Kind, request.Title, request.Body, string.Join(',', labels));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>Whether anything finished is in here.</summary>
    public bool HasResults => Analysis is not null || Coverage is not null || Design is not null || BugAnalysis is not null;

    /// <summary>The names of the finished stages, for the comment that says what is reused.</summary>
    public IReadOnlyList<string> Kept => new[]
    {
        Analysis is null && BugAnalysis is null ? null : "the analysis",
        Coverage is null ? null : "the coverage check",
        Design is null ? null : "the design",
    }.OfType<string>().ToList();

    /// <summary>Null when this record may be used for this run; otherwise why not (a sentence that continues "I start from the beginning, because ...").</summary>
    /// <param name="changedSince">The files the default branch changed since the earlier run (null when that could not be found out). New commits that only touched
    /// files the stages do not read (see <see cref="IsUnrelated"/>) do not make the saved results stale.</param>
    public string? WhyNot(WorkRequest request, WorkspaceInfo workspace, IReadOnlyList<string>? changedSince = null) =>
        Version != CurrentVersion ? "the earlier run was saved by another version of the factory"
        : Fingerprint != FingerprintOf(request) ? "the ticket's text or labels changed since the earlier run"
        : HeadCommit != workspace.HeadCommit && !(changedSince is { Count: > 0 } && changedSince.All(IsUnrelated))
            ? "the default branch has new commits since the earlier run, so what it found may be out of date"
        : null;

    /// <summary>
    /// A file whose change cannot make an earlier analysis, coverage check or design out of date: a Markdown document other than <c>AGENTS.md</c> (which the agents are told to
    /// read as the project's rules) and nothing under <c>.factory/</c> (the project's settings). Code, tests, configuration and everything else count.
    /// </summary>
    public static bool IsUnrelated(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith(".factory/", StringComparison.Ordinal)
        && !string.Equals(System.IO.Path.GetFileName(path), "AGENTS.md", StringComparison.OrdinalIgnoreCase);
}
