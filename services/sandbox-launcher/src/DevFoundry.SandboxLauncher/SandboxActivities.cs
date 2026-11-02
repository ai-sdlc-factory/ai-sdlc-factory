using Temporalio.Activities;

namespace DevFoundry.SandboxLauncher;

public sealed class SandboxActivities(SandboxRunner runner, SandboxSettings settings)
{
    [Activity(SandboxNames.ActivityName)]
    public Task<SandboxRunResult> RunAsync(SandboxRunRequest request)
    {
        var run = SandboxPolicy.Validate(request, settings, WorkspaceExists);
        var context = ActivityExecutionContext.Current;
        return runner.RunAsync(run, () => context.Heartbeat(), context.CancellationToken);
    }

    // Docker Desktop silently creates missing bind sources as root, so check before asking it to mount.
    // A symlink is refused: it could point outside the workspace root.
    private static bool WorkspaceExists(string path)
    {
        var directory = new DirectoryInfo(path);
        return directory.Exists && directory.LinkTarget is null;
    }
}
