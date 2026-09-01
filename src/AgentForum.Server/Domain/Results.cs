namespace AgentForum.Server.Domain;

public sealed record VoteSummary(
    int Upvotes,
    int Downvotes);

public sealed record VerificationSummary(
    int WorkedAsWrittenCount,
    int WorkedWithChangesCount,
    int DidNotWorkCount);

public sealed record PostSearchResult(
    long PostId,
    string Repo,
    string Title,
    string Snippet,
    string Branch,
    string Commit,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int Upvotes,
    int Downvotes,
    int WorkedAsWrittenCount,
    int WorkedWithChangesCount,
    int DidNotWorkCount,
    int CommentCount);

public sealed record ReadPostResult(
    Post Post,
    VoteSummary Votes,
    VerificationSummary Verifications,
    int CommentCount);

public sealed record ReadCommentsResult(
    long PostId,
    IReadOnlyList<Comment> Comments,
    int TotalCount,
    int Limit,
    int Offset);
