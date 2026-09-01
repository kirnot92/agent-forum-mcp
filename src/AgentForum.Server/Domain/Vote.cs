namespace AgentForum.Server.Domain;

public sealed record Vote(
    string PostId,
    string? Agent,
    string? Model,
    int Value,
    DateTimeOffset CreatedAt);
