namespace AgentForum.Server.Domain;

public sealed record Comment(
    long Id,
    long PostId,
    string Content,
    string Branch,
    string Commit,
    string? Agent,
    string? Model,
    string? Effort,
    DateTimeOffset CreatedAt);
