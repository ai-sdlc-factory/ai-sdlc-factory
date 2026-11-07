namespace DevFoundry.Controller;

/// <summary>
/// The short words a run ends with (<see cref="WorkResult.Status"/>). Every one is described in docs/workflows.md, and
/// <c>tools/check-docs.py</c> fails if one is missing there.
/// </summary>
public static class Statuses
{
    /// <summary>the coverage check found every criterion already met.</summary>
    public const string AlreadyImplemented = "already_implemented";

    /// <summary>The analyst's answer could not be used, even after one retry.</summary>
    public const string AnalysisFailed = "analysis_failed";

    /// <summary>The incident analyst's answer could not be used, even after one retry.</summary>
    public const string AssessmentFailed = "assessment_failed";

    /// <summary>OpenCode is not signed in (login guide) (saved if code existed).</summary>
    public const string Blocked = "blocked";

    /// <summary>the developer said it cannot continue (for example the ticket needs a protected file changed) (saved).</summary>
    public const string BuildBlocked = "build_blocked";

    /// <summary>the build rounds (3 by default) did not get through the checks and review (work saved to a -wip branch).</summary>
    public const string BuildFailed = "build_failed";

    /// <summary>bug: no test that fails because of the bug was produced in 3 attempts.</summary>
    public const string CannotReproduce = "cannot_reproduce";

    /// <summary>a person closed the ticket.</summary>
    public const string Closed = "closed";

    /// <summary>The coverage check's answer could not be used, even after one retry.</summary>
    public const string CoverageFailed = "coverage_failed";

    /// <summary>The architect's answer could not be used, even after one retry.</summary>
    public const string DesignFailed = "design_failed";

    /// <summary>The architect said the change cannot or should not be made as asked.</summary>
    public const string DesignRejected = "design_rejected";

    /// <summary>The diagnostician's answer could not be used, even after one retry.</summary>
    public const string DiagnosisFailed = "diagnosis_failed";

    /// <summary>an unexpected error; the ticket says so (saved if code existed).</summary>
    public const string Error = "error";

    /// <summary>the repository's own .factory/Dockerfile could not be read or built (see a project's own image).</summary>
    public const string ImageFailed = "image_failed";

    /// <summary>The investigator's answer could not be used, even after one retry.</summary>
    public const string InvestigationFailed = "investigation_failed";

    /// <summary>The mitigation advisor's answer could not be used, even after one retry.</summary>
    public const string MitigationFailed = "mitigation_failed";

    /// <summary>the repository has no usable .factory/project.json.</summary>
    public const string NeedsConfig = "needs_config";

    /// <summary>bug: the cause is larger than a bug fix; open a feature ticket.</summary>
    public const string NeedsDesign = "needs_design";

    /// <summary>the rounds of questions were used up; a person has to decide.</summary>
    public const string NeedsHuman = "needs_human";

    /// <summary>the work produced nothing to publish.</summary>
    public const string NoChanges = "no_changes";

    /// <summary>revision: a /fix with no text and no review to act on.</summary>
    public const string NothingToDo = "nothing_to_do";

    /// <summary>The postmortem could not be drafted or did not pass its checks.</summary>
    public const string PostmortemFailed = "postmortem_failed";

    /// <summary>incident: the draft postmortem pull request is open.</summary>
    public const string PostmortemOpened = "postmortem_opened";

    /// <summary>feature or bug: the pull request is open.</summary>
    public const string PrOpened = "pr_opened";

    /// <summary>publishing or opening the pull request failed (saved, or the branch is named in the comment).</summary>
    public const string PublishFailed = "publish_failed";

    /// <summary>The analyst judged the ticket not buildable in this repository.</summary>
    public const string Rejected = "rejected";

    /// <summary>The reproducer's answer could not be used, even after one retry.</summary>
    public const string ReproductionFailed = "reproduction_failed";

    /// <summary>a review or QA answer stayed unusable; nothing was published (saved).</summary>
    public const string ReviewFailed = "review_failed";

    /// <summary>revision: the project's limit of revisions (3 by default) was already reached on this pull request.</summary>
    public const string RevisionLimit = "revision_limit";

    /// <summary>A revision was pushed as one new commit on the pull request's own branch.</summary>
    public const string RevisionPushed = "revision_pushed";

    /// <summary>A revision changed no files; the answers were posted on the pull request instead.</summary>
    public const string RevisionReplied = "revision_replied";

    /// <summary>the workspace could not be prepared.</summary>
    public const string SetupFailed = "setup_failed";

    /// <summary>nobody answered for 7 days.</summary>
    public const string TimedOut = "timed_out";
}
