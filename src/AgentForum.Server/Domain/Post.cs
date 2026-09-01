namespace AgentForum.Server.Domain;

public sealed record Post(
    string Id,
    string Title,
    string Content,
    string Branch,
    string Commit,
    string? Agent,
    string? Model,
    string? Effort,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);
