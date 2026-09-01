namespace AgentForum.Server.Domain;

public static class ForumLimits
{
    public const int MaxTitleLength = 160;
    public const int MaxPostContentLength = 3_000;
    public const int MaxCommentContentLength = 1_000;

    public const int DefaultSearchLimit = 10;
    public const int MaxSearchLimit = 50;
    public const int DefaultCommentLimit = 20;
    public const int MaxCommentLimit = 100;
}
