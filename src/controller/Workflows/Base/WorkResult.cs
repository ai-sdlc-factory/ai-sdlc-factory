namespace AiSdlcFactory.Controller;

/// <summary>
/// How a run ended, whatever its kind (feature, bug, incident or pull request revision). <see cref="Status"/> is one of the short words listed
/// in docs/workflows.md; the rest is what a caller may want to link to. <see cref="Usage"/> is what all the agent runs of this workflow
/// used, as OpenCode reported it (null when no agent ran), so it is kept in Temporal for every run whether or not the project asks for a record.
/// </summary>
public sealed record WorkResult(
    string Status, bool? NeedsArchitect = null, string? PullRequestUrl = null, string? WorkBranch = null, TokenUsage? Usage = null);
