namespace AiSdlcFactory.Controller;

/// <summary>When a QA run goes ahead: only on an open pull request that the factory itself opened, from a branch in the same repository.</summary>
public static class QaRules
{
    public static RevisionRefusal? Refuse(QaRequest request, QaContext context)
    {
        if (context.Pull is not { } pull)
        {
            return new RevisionRefusal(Statuses.QaNoPullRequest, TicketComments.QaNoPullRequest);
        }
        if (!RevisionRules.FactoryBranch().IsMatch(pull.HeadRef) || !string.Equals(pull.Author, context.BotLogin, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pull.HeadRepository, pull.BaseRepository, StringComparison.OrdinalIgnoreCase))
        {
            // A comment on somebody else's pull request is none of the factory's business.
            return new RevisionRefusal(request.OnPullRequest ? "ignored" : Statuses.QaNoPullRequest, request.OnPullRequest ? null : TicketComments.QaNoPullRequest);
        }
        if (pull.Merged || !string.Equals(pull.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            return new RevisionRefusal("ignored", TicketComments.QaClosed);
        }
        return null;
    }
}
