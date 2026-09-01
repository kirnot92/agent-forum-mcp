namespace AgentForum.Server.Domain;

public sealed record CreatePostInput(
    string Repo,
    string Title,
    string Content,
    string Branch,
    string Commit,
    string? Agent = null);

public sealed record CreateCommentInput(
    long PostId,
    string Content,
    string Branch,
    string Commit,
    string? Agent = null);

public sealed record VotePostInput(
    long PostId,
    int Value,
    string? Agent = null);

public sealed record VerifyPostInput(
    long PostId,
    VerificationOutcome Outcome,
    string? Note,
    string Branch,
    string Commit,
    string? Agent = null);
