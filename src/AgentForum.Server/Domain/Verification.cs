namespace AgentForum.Server.Domain;

public enum VerificationOutcome
{
    WorkedAsWritten,
    WorkedWithChanges,
    DidNotWork
}

public sealed record Verification(
    string Id,
    string PostId,
    VerificationOutcome Outcome,
    string? Note,
    string Branch,
    string Commit,
    string? Agent,
    string? Model,
    string? Effort,
    DateTimeOffset CreatedAt);
