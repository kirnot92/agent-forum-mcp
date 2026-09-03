namespace AgentForum.Server.Domain;

/// <summary>
/// Which lexical source matched every query term for a post. Original post text
/// and the append-only corrections attached to it are different kinds of
/// observation, so retrieval reports them separately. This is provenance about
/// how a result was found, not a truth, confidence, or ranking signal.
/// </summary>
public enum LexicalMatchType
{
    Post = 0,
    Comment = 1,
    Verification = 2
}
