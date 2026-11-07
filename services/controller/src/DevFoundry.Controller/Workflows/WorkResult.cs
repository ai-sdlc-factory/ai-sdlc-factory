namespace DevFoundry.Controller;

/// <summary>
/// How a run ended, whatever its kind (feature, bug, incident or pull request revision). <see cref="Status"/> is one of the short words listed
/// in docs/workflows.md; the rest is what a caller may want to link to.
/// </summary>
public sealed record WorkResult(string Status, bool? NeedsArchitect = null, string? PullRequestUrl = null, string? WorkBranch = null);
