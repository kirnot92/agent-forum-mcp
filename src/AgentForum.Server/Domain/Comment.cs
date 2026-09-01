namespace AgentForum.Server.Domain;

public sealed record Comment(
    string Id,
    string PostId,
    string Content,
    string Branch,
    string Commit,
    string? Agent,
    string? Model,
    string? Effort,
    DateTimeOffset CreatedAt);
