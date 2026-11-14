namespace DevFoundry.Controller;

/// <summary>
/// Ends a run at once, from anywhere in its stages, with a status and (usually) a comment: the run's own token budget is used up, a daily limit cannot be waited out because the
/// ticket was closed, or the model provider says its usage limit is reached. Thrown and caught inside the workflow's own code (<c>RunPipelineAsync</c>), so it never reaches Temporal;
/// the run then ends like any other that stopped with work, and what was written so far is saved to a work-in-progress branch.
/// </summary>
public sealed class RunStopException(string status, string? comment) : Exception(comment ?? status)
{
    public string Status { get; } = status;

    /// <summary>What to tell the ticket, or null to say nothing (the ticket was closed by a person, who knows).</summary>
    public string? Comment { get; } = comment;
}
