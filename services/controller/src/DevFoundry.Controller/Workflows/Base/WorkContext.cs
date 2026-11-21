namespace DevFoundry.Controller;

/// <summary>
/// What nearly every step of a run needs together: the ticket (or pull request) it is about, the checkout it works in, and the project's
/// settings. Passed as one value so a step is not declared with the same three parameters every time.
/// </summary>
public sealed record WorkContext(WorkRequest Request, WorkspaceInfo Workspace, ProjectConfig Project);
