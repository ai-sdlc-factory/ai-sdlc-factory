using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public sealed record FeatureResult(string Status, bool? NeedsArchitect = null, string? PullRequestUrl = null, string? WorkBranch = null);

/// <summary>
/// One run per feature ticket, following docs/feature-factory.md. Built so far: intake (a private
/// workspace and the project's configuration), the analyst, the coverage check, triage, the architect when
/// triage asks for it, and the build (developer, automated checks, pull request). The workflow code owns every decision; agents only return a verdict
/// value, which is validated before it is used.
/// </summary>
[Workflow("FeatureWorkflow")]
public sealed partial class FeatureWorkflow
{
    private static readonly TimeSpan WaitForHuman = TimeSpan.FromDays(7);
    private const int MaxClarificationRounds = 4;
    private const int MaxDesignRounds = 3;
    private const int MaxAgentAttempts = 2;

    private readonly List<HumanReply> replies = [];
    private bool ticketClosed;

    // The ticket this run is for, and whether any later stage can still take a human comment: the analyst and the
    // architect read them, the build does not.
    private FeatureRequest? current;
    private bool commentsCanStillBeUsed = true;
    private bool tooLateNoticePosted;

    [WorkflowSignal("human_reply")]
    public async Task OnHumanReplyAsync(HumanReply reply)
    {
        if (current is not null && !commentsCanStillBeUsed)
        {
            // One notice per run is enough, however many comments follow.
            if (!tooLateNoticePosted)
            {
                tooLateNoticePosted = true;
                await CommentAsync(current, TicketComments.CommentTooLate);
            }
            return;
        }
        replies.Add(reply);
    }

    [WorkflowSignal("ticket_closed")]
    public Task OnTicketClosedAsync()
    {
        ticketClosed = true;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<FeatureResult> RunAsync(FeatureRequest request)
    {
        current = request;
        await CommentAsync(request, "The factory picked up this feature ticket. Preparing a workspace.");

        try
        {
            WorkspaceInfo workspace;
            try
            {
                workspace = await Workflow.ExecuteActivityAsync(
                    (WorkspaceActivities activities) => activities.PrepareAsync(
                        new WorkspaceRequest(request.Owner, request.Repo, request.WorkflowId, $"factory/issue-{request.IssueNumber}")),
                    new ActivityOptions
                    {
                        StartToCloseTimeout = TimeSpan.FromMinutes(5),
                        RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
                    });
            }
            catch (ActivityFailureException error)
            {
                await CommentAsync(request, $"Setting up the workspace failed: {Short(error.InnerException?.Message ?? error.Message)}");
                return new FeatureResult("setup_failed");
            }

            var configText = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.ReadProjectConfigAsync(request.WorkflowId),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(1) });
            if (!ProjectConfigParser.TryParse(configText, out var project, out var configError))
            {
                await CommentAsync(request, configText is null ? NoConfigMessage : $"`.factory/project.json` cannot be used: {TicketComments.Inert(configError)}. Fix it, then close and reopen this ticket.");
                return new FeatureResult("needs_config");
            }

            if (project.Record)
            {
                recordDirectory = RunRecord.DirectoryFor(request.IssueNumber, Workflow.Info.RunId ?? "run");
            }

            FeatureResult result;
            try
            {
                result = await StagesAsync(request, workspace, project);
            }
            catch (ActivityFailureException error)
            {
                // Without this the ticket would just go quiet while the run shows as failed in Temporal.
                await CommentAsync(request, $"The factory hit an unexpected error and stopped: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))}");
                result = new FeatureResult("error");
            }
            await NoteUnusedCommentsAsync(request, result);
            return await SaveUnfinishedWorkAsync(request, result);
        }
        finally
        {
            // Runs even if the workflow was cancelled, so checkouts do not pile up on the host.
            await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.CleanupAsync(request.WorkflowId),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(1),
                    CancellationToken = CancellationToken.None,
                });
        }
    }

    /// <summary>
    /// Comments that arrived but were never read by a stage (nothing asked a question afterwards) are reported once at
    /// the end, so a person is not left wondering. A ticket a person closed needs no such reply.
    /// </summary>
    private async Task NoteUnusedCommentsAsync(FeatureRequest request, FeatureResult result)
    {
        commentsCanStillBeUsed = false;
        if (replies.Count > 0 && !ticketClosed && result.Status != "closed")
        {
            var count = replies.Count;
            replies.Clear();
            await CommentAsync(request, TicketComments.CommentsNotUsed(count));
        }
    }

    // Endings after which code may exist that nobody has seen; a run a person closed, or one that stopped before the build, has none.
    private static readonly HashSet<string> StoppedWithWork = ["build_failed", "build_blocked", "review_failed", "publish_failed", "blocked", "error"];

    // For these the ticket always gets a summary, even when there was nothing to save.
    private static readonly HashSet<string> AlwaysExplained = ["build_failed", "build_blocked", "review_failed", "publish_failed", "error"];

    /// <summary>
    /// When a run stops without publishing, push what it had written to a work-in-progress branch and say so on the
    /// ticket, with a link to the full record of the run. Saving is best effort: it never replaces the real outcome.
    /// </summary>
    private async Task<FeatureResult> SaveUnfinishedWorkAsync(FeatureRequest request, FeatureResult result)
    {
        // Already on a branch (only the pull request failed), or nothing that could hold unsaved work.
        if (!StoppedWithWork.Contains(result.Status) || result.WorkBranch is not null)
        {
            return result;
        }

        RescueResult rescue;
        try
        {
            rescue = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.RescueAsync(new RescueRequest(
                    request.WorkflowId,
                    $"WIP: {PullRequestText.Title(request)} (#{request.IssueNumber}), not finished\n\n" +
                    $"Saved by the factory when it stopped ({result.Status}). Not reviewed, and the checks did not all pass.",
                    RecordSpec(request, $"Stopped before finishing ({result.Status}). The work so far is on this work-in-progress branch; it was not reviewed and the checks did not all pass."))),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(5), RetryPolicy = new RetryPolicy { MaximumAttempts = 2 } });
        }
        catch (ActivityFailureException error)
        {
            await CommentAsync(request, $"The factory stopped ({result.Status}) and could not save the work so far: {TicketComments.Inert(Short(error.InnerException?.Message ?? error.Message))}");
            return result;
        }

        if (rescue.Saved || AlwaysExplained.Contains(result.Status))
        {
            await CommentAsync(request, TicketComments.Stopped(result.Status, rescue));
        }
        return rescue.Saved ? result with { WorkBranch = rescue.Branch } : result;
    }

    /// <summary>
    /// The stages after intake, in order: analyst, coverage check, triage. Each either stops the
    /// workflow with a status or hands its result to the next one.
    /// </summary>
    private async Task<FeatureResult> StagesAsync(FeatureRequest request, WorkspaceInfo workspace, ProjectConfig project)
    {
        var analysis = await AnalyseAsync(request, workspace, project.Image);
        if (analysis.Stop is { } stop)
        {
            return stop;
        }
        var verdict = analysis.Verdict!;

        var coverage = await CheckCoverageAsync(request, workspace, project.Image, verdict);
        if (coverage.Stop is { } stopped)
        {
            return stopped;
        }

        var triage = Triage.Decide(verdict.Complexity, request.Labels ?? []);
        await CommentAsync(request, TicketComments.TriageDecision(triage));

        ArchitectDesign? approved = null;
        if (triage.NeedsArchitect)
        {
            var design = await DesignAsync(request, workspace, project.Image, coverage.Verdict!, triage);
            if (design.Stop is { } designStop)
            {
                return designStop;
            }
            approved = design.Verdict;
        }

        // From here no stage asks a question, so a new comment can no longer change the outcome.
        commentsCanStillBeUsed = false;
        return await BuildAsync(request, workspace, project, coverage.Verdict!.Remaining.ToList(), approved, triage);
    }

    private const string NoConfigMessage =
        "This repository has no `.factory/project.json`, so I do not know which sandbox image or checks to use. " +
        "Add one on the default branch, for example:\n\n```json\n" +
        "{\n  \"image\": \"python\",\n  \"gates\": [\n    { \"name\": \"test\", \"command\": [\"python\", \"-m\", \"pytest\", \"-q\"] }\n  ]\n}\n```\n\n" +
        "Then close and reopen this ticket.";

    /// <summary>
    /// The network each agent role gets: the developer may also fetch packages, every other agent can reach only
    /// the model provider. Which hosts a profile allows is decided in egress/profiles.json, not here.
    /// </summary>
    public static string NetworkFor(string role) => role == "developer" ? SandboxNetworks.Developer : SandboxNetworks.Model;

    private const string NotSignedInMessage =
        "OpenCode is not signed in, so I cannot run the agents. Run the `opencode-login` service, then close and reopen this ticket.";

    private void DrainReplies(List<string> answers)
    {
        foreach (var reply in replies)
        {
            answers.Add(reply.Body);
        }
        replies.Clear();
    }

    /// <summary>Keeps repository- or model-supplied text short and free of characters that break the comment's formatting.</summary>
    private static string Short(string text)
    {
        var flat = text.Replace('`', '\'').Replace('\n', ' ').Trim();
        return flat.Length > 200 ? flat[..200] + "..." : flat;
    }

    private static Task CommentAsync(FeatureRequest request, string body) =>
        Workflow.ExecuteActivityAsync(
            (ForgejoActivities activities) => activities.PostIssueCommentAsync(
                new CommentRequest(request.Owner, request.Repo, request.IssueNumber, body)),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromSeconds(30),
                RetryPolicy = new RetryPolicy { MaximumAttempts = 5 },
            });
}
