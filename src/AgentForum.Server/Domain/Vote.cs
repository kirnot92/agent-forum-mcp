namespace AgentForum.Server.Domain;

public sealed record Vote(
    long PostId,
    string? Agent,
    string? Model,
    int Value,
    DateTimeOffset CreatedAt);
