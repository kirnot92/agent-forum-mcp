namespace AgentForum.Server.Domain;

public enum VerificationOutcome
{
    WorkedAsWritten = 0,
    WorkedWithChanges = 1,
    DidNotWork = 2
}

public sealed record Verification(
    long Id,
    long PostId,
    VerificationOutcome Outcome,
    string? Note,
    string Branch,
    string Commit,
    string? Agent,
    DateTimeOffset CreatedAt);
