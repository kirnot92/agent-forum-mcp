namespace AgentForum.Server.Domain;

public sealed record Vote(
    long PostId,
    string? Agent,
    int Value,
    DateTimeOffset CreatedAt);
