using Temporalio.Activities;
using Temporalio.Exceptions;

namespace DevFoundry.SandboxLauncher;

public sealed class SandboxActivities(SandboxRunner runner, SandboxSettings settings, IDockerClient docker, RepoImageBuilder images)
{
    [Activity(SandboxNames.ActivityName)]
    public async Task<SandboxRunResult> RunAsync(SandboxRunRequest request)
    {
        var context = ActivityExecutionContext.Current;
        // An image built from a repository is known by a content-derived alias. It is usable only while the switch is on and
        // the image really is one we built (it carries our label), never an arbitrary local image.
        string? repoTag = null;
        if (settings.AllowRepoImages && RepoImageNames.IsAlias(request.Image))
        {
            var tag = RepoImageNames.TagFor(request.Image);
            if (await docker.InspectImageAsync(tag, context.CancellationToken) is { } found && found.Labels.ContainsKey(RepoImageNames.Label))
            {
                repoTag = tag;
            }
        }
        var run = SandboxPolicy.Validate(request, settings, WorkspaceExists, alias => alias == request.Image ? repoTag : null);
        return await runner.RunAsync(run, () => context.Heartbeat(), context.CancellationToken);
    }

    /// <summary>Builds the image a repository describes, once per distinct content. See <see cref="RepoImagePolicy"/> for what is accepted.</summary>
    [Activity(SandboxNames.BuildActivityName)]
    public Task<SandboxBuildResult> BuildImageAsync(SandboxBuildRequest request)
    {
        var context = ActivityExecutionContext.Current;
        return images.BuildAsync(request, () => context.Heartbeat(), context.CancellationToken);
    }

    // Docker Desktop silently creates missing bind sources as root, so check before asking it to mount.
    // A symlink is refused: it could point outside the workspace root.
    private static bool WorkspaceExists(string path)
    {
        var directory = new DirectoryInfo(path);
        return directory.Exists && directory.LinkTarget is null;
    }
}
