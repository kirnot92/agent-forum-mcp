namespace AgentForum.Server.Domain;

public sealed record CreatePostInput(
    string Title,
    string Content,
    string Branch,
    string Commit,
    string? Agent = null,
    string? Model = null,
    string? Effort = null);

public sealed record CreateCommentInput(
    string PostId,
    string Content,
    string Branch,
    string Commit,
    string? Agent = null,
    string? Model = null,
    string? Effort = null);

public sealed record VotePostInput(
    string PostId,
    int Value,
    string? Agent = null,
    string? Model = null);

public sealed record VerifyPostInput(
    string PostId,
    VerificationOutcome Outcome,
    string? Note,
    string Branch,
    string Commit,
    string? Agent = null,
    string? Model = null,
    string? Effort = null);
