using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace DevFoundry.Controller;

public abstract partial class FactoryWorkflowBase
{
    /// <summary>
    /// A repository may bring its own sandbox image: a <c>.factory/Dockerfile</c> (and a small <c>.factory/docker/</c> context) that only a person
    /// can change, read from the default branch. If the operator allows it, the launcher builds it (once per distinct content) and every run for this
    /// ticket uses the result instead of the standard image named in <c>project.json</c>. If the operator has not allowed it, the file is ignored and
    /// the ticket says so. Without the file, nothing changes. A broken Dockerfile stops the run instead of silently falling back, because the checks
    /// would then run in an environment the project did not intend.
    /// </summary>
    protected async Task<(string Image, WorkResult? Stop)> PrepareProjectImageAsync(WorkRequest request, ProjectConfig project, FactoryDefaults defaults)
    {
        RepoImageSource? source;
        try
        {
            source = await Workflow.ExecuteActivityAsync(
                (WorkspaceActivities activities) => activities.ReadProjectImageAsync(request.WorkflowId),
                ActivityPolicy.Local(TimeSpan.FromMinutes(1), 1));
        }
        catch (ActivityFailureException error)
        {
            return (project.Image, await ImageFailedAsync(request, error.InnerException?.Message ?? error.Message));
        }
        if (source is null)
        {
            return (project.Image, null);
        }

        if (!defaults.AllowRepoImages)
        {
            await CommentAsync(request,
                $"This repository has a `.factory/Dockerfile`, but repository images are switched off in this factory, so I am using the standard `{project.Image}` image. " +
                "An operator can switch them on with `FACTORY_ALLOW_REPO_IMAGES`.");
            return (project.Image, null);
        }

        await CommentAsync(request, "Preparing the sandbox image from `.factory/Dockerfile` (the first build can take a few minutes; later runs reuse it).");
        try
        {
            var built = await Workflow.ExecuteActivityAsync<SandboxBuildResult>(
                SandboxNames.BuildActivityName,
                [new SandboxBuildRequest(source.Dockerfile, source.Files)],
                // A long RUN step prints nothing for a while, so the heartbeat limit is generous; it only catches a launcher that died.
                ActivityPolicy.Sandbox(TimeSpan.FromMinutes(35), attempts: 1, heartbeat: TimeSpan.FromMinutes(10)));
            if (built.Built)
            {
                await CommentAsync(request, $"The sandbox image is ready (built in {built.Seconds:0} s, {built.SizeBytes / (1024 * 1024)} MB, `{built.Image}`).");
            }
            return (built.Image, null);
        }
        catch (ActivityFailureException error)
        {
            return (project.Image, await ImageFailedAsync(request, error.InnerException?.Message ?? error.Message));
        }
    }

    private async Task<WorkResult> ImageFailedAsync(WorkRequest request, string reason)
    {
        await CommentAsync(request,
            $"The project's sandbox image could not be used, so I stopped: {TicketComments.Inert(Short(reason))}\n\n" +
            $"Fix `.factory/Dockerfile` on the default branch, then {(request.Kind == WorkIds.Revision ? "ask again with `/fix`." : "close and reopen this ticket.")}");
        return new WorkResult("image_failed");
    }
}
