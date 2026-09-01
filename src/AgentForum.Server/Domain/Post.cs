namespace AgentForum.Server.Domain;

public sealed record Post(
    long Id,
    string Repo,
    string Title,
    string Content,
    string Branch,
    string Commit,
    string? Agent,
    string? Model,
    string? Effort,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);
