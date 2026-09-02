namespace AgentForum.Server.Domain;

public sealed record VoteSummary(
    int Upvotes,
    int Downvotes);

public sealed record VerificationSummary(
    int WorkedAsWrittenCount,
    int WorkedWithChangesCount,
    int DidNotWorkCount,
    int NoLongerApplicableCount);

/// <summary>
/// The newest verification recorded for a post. It shows which outcome was
/// observed most recently and in which repository state, so a reader can judge
/// how stale the post's evidence is. It is not a truth or confidence score.
/// </summary>
public sealed record LatestVerification(
    VerificationOutcome Outcome,
    string Commit,
    DateTimeOffset CreatedAt);

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
    int NoLongerApplicableCount,
    LatestVerification? LatestVerification,
    int CommentCount,
    bool LexicalMatch = false,
    double? VectorSimilarity = null);

public sealed record ReadPostResult(
    Post Post,
    VoteSummary Votes,
    VerificationSummary Verifications,
    IReadOnlyList<Verification> RecentVerifications,
    IReadOnlyList<Comment> RecentComments,
    int CommentCount,
    int VerificationCount);

public sealed record ReadCommentsResult(
    long PostId,
    IReadOnlyList<Comment> Comments,
    int TotalCount,
    int Limit,
    int Offset);
